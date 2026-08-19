// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Locks;

using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using ktsu.GitLfsCache.Configuration;
using ktsu.GitLfsCache.Observability;
using ktsu.GitLfsCache.Upstreams;
using Microsoft.Extensions.Options;

/// <summary>
/// Issues the individual lock calls of a batched request, in parallel and under a limiter.
/// </summary>
/// <remarks>
/// This is a parallelizer, not a lock authority. Every item is one upstream call carrying the
/// caller's own Authorization header, and upstream grants or refuses each one under the caller's
/// identity exactly as it would have done had git-lfs issued them itself. The proxy decides only the
/// order and the concurrency.
/// <para>
/// The operation is not atomic and cannot be made atomic over an API with no transaction. A caller
/// that disconnects part way through leaves the locks already granted still granted. That is the same
/// property sequential locking has, made visible in a result array instead of hidden in an
/// interrupted loop.
/// </para>
/// </remarks>
/// <param name="upstreamClient">Sends requests upstream.</param>
/// <param name="limiter">Bounds how hard one upstream is pushed.</param>
/// <param name="snapshots">Resolves a path to a lock id when releasing by path.</param>
/// <param name="refresher">Refreshes a snapshot whose ids turned out to be stale.</param>
/// <param name="metrics">Cache counters.</param>
/// <param name="options">The configured options.</param>
public sealed class LockFanOut(
	IUpstreamClient upstreamClient,
	IUpstreamLimiter limiter,
	ILockSnapshotStore snapshots,
	ILockListRefresher refresher,
	CacheMetrics metrics,
	IOptions<GitLfsCacheOptions> options)
{
	/// <summary>
	/// Runs every item of a batched request.
	/// </summary>
	/// <param name="request">The parsed request.</param>
	/// <param name="key">The repository being operated on.</param>
	/// <param name="upstreamBase">The configured upstream base URL.</param>
	/// <param name="authorization">The caller's Authorization header.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>One result per item, in the order the client sent them.</returns>
	public async Task<JsonObject> ExecuteAsync(
		LockFanOutRequest request,
		LockSnapshotKey key,
		Uri upstreamBase,
		string? authorization,
		CancellationToken cancellationToken)
	{
		Ensure.NotNull(request);
		Ensure.NotNull(key);

		// Indexed so results can be written back in the order the client sent them, even though they
		// complete in whatever order upstream answers.
		JsonObject?[] results = new JsonObject?[request.Targets.Count];

		await Parallel.ForAsync(
			0,
			request.Targets.Count,
			new ParallelOptions
			{
				MaxDegreeOfParallelism = options.Value.Locks.MaxFanOutConcurrency,
				CancellationToken = cancellationToken,
			},
			async (index, token) =>
			{
				results[index] = await RunOneAsync(
					request,
					request.Targets[index],
					key,
					upstreamBase,
					authorization,
					token).ConfigureAwait(false);
			}).ConfigureAwait(false);

		// One lock changed means the listing is wrong, and a caller who just locked five hundred files
		// is about to look at them.
		if (results.Any(result => JsonValues.Bool(result?["ok"]) == true))
		{
			snapshots.Invalidate(key);
		}

		JsonArray array = [];

		foreach (JsonObject? result in results)
		{
			array.Add(result ?? Failure(null, null, HttpStatusCode.InternalServerError, "not attempted"));
		}

		return new JsonObject { ["results"] = array };
	}

	private static JsonObject Failure(string? path, string? id, HttpStatusCode status, string message)
	{
		JsonObject result = new()
		{
			["ok"] = false,
			["status"] = (int)status,
			["message"] = message,
		};

		if (path is not null)
		{
			result["path"] = path;
		}

		if (id is not null)
		{
			result["id"] = id;
		}

		return result;
	}

	private async Task<JsonObject> RunOneAsync(
		LockFanOutRequest request,
		LockFanOutTarget target,
		LockSnapshotKey key,
		Uri upstreamBase,
		string? authorization,
		CancellationToken cancellationToken)
	{
		string? id = target.Id;

		if (request.Operation == LockFanOutOperation.Unlock && id is null)
		{
			id = ResolveId(key, target.Path!);

			if (id is null)
			{
				// The snapshot may simply be older than the lock. One refresh, then one more look,
				// before telling the caller a lock they can see does not exist.
				await RefreshForResolutionAsync(key, upstreamBase, authorization, cancellationToken)
					.ConfigureAwait(false);

				id = ResolveId(key, target.Path!);
			}

			if (id is null)
			{
				return Failure(target.Path, null, HttpStatusCode.NotFound, "no lock is held on that path");
			}
		}

		return await SendWithRetriesAsync(
			request,
			target,
			id,
			key,
			upstreamBase,
			authorization,
			cancellationToken).ConfigureAwait(false);
	}

	private async Task<JsonObject> SendWithRetriesAsync(
		LockFanOutRequest request,
		LockFanOutTarget target,
		string? id,
		LockSnapshotKey key,
		Uri upstreamBase,
		string? authorization,
		CancellationToken cancellationToken)
	{
		int attempts = options.Value.Locks.MaxFanOutRetries + 1;

		for (int attempt = 0; attempt < attempts; attempt++)
		{
			metrics.RecordLockFanOutItem(key.Upstream);

			using IDisposable slot = await limiter
				.AcquireAsync(key.Upstream, cancellationToken)
				.ConfigureAwait(false);

			using HttpRequestMessage message = Build(request, target, id, key, upstreamBase, authorization);

			using HttpResponseMessage response = await upstreamClient
				.SendAsync(message, cancellationToken)
				.ConfigureAwait(false);

			if (TryGetThrottle(response, out TimeSpan retryAfter))
			{
				// The whole upstream pauses, not just this item. A forge that throttled one call is
				// describing its own state, and pushing the rest through would earn a longer ban.
				metrics.RecordLockFanOutThrottled(key.Upstream);
				limiter.Throttle(key.Upstream, retryAfter);
				continue;
			}

			string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

			if (!response.IsSuccessStatusCode)
			{
				return Failure(target.Path, id, response.StatusCode, Describe(body, response.StatusCode));
			}

			metrics.RecordLockFanOutSucceeded(key.Upstream);
			return Success(target.Path, id, body);
		}

		return Failure(
			target.Path,
			id,
			HttpStatusCode.TooManyRequests,
			"upstream kept throttling this call");
	}

	private static HttpRequestMessage Build(
		LockFanOutRequest request,
		LockFanOutTarget target,
		string? id,
		LockSnapshotKey key,
		Uri upstreamBase,
		string? authorization)
	{
		JsonObject body = [];

		if (request.Ref is string name)
		{
			body["ref"] = new JsonObject { ["name"] = name };
		}

		if (request.Operation == LockFanOutOperation.Lock)
		{
			body["path"] = target.Path;

			return UpstreamRequests.BuildLockCreateRequest(
				upstreamBase,
				key.RepositoryPath,
				body.ToJsonString(),
				authorization);
		}

		body["force"] = request.Force;

		return UpstreamRequests.BuildUnlockRequest(
			upstreamBase,
			key.RepositoryPath,
			id!,
			body.ToJsonString(),
			authorization);
	}

	private string? ResolveId(LockSnapshotKey key, string path)
	{
		LockSnapshot? snapshot = snapshots.Read(key);

		return snapshot?.Filter(path, id: null) is [LockEntry entry, ..]
			? entry.Id
			: null;
	}

	private async Task RefreshForResolutionAsync(
		LockSnapshotKey key,
		Uri upstreamBase,
		string? authorization,
		CancellationToken cancellationToken)
	{
		LockRefreshResult result = await refresher
			.RefreshAsync(key, upstreamBase, authorization, cancellationToken)
			.ConfigureAwait(false);

		if (result.Outcome == LockRefreshOutcome.Succeeded)
		{
			snapshots.Publish(key, result.Snapshot!);
		}
	}

	private static JsonObject Success(string? path, string? id, string body)
	{
		JsonObject result = new() { ["ok"] = true };

		if (path is not null)
		{
			result["path"] = path;
		}

		if (id is not null)
		{
			result["id"] = id;
		}

		// Upstream's own lock object is passed straight through, so a client learns the id, the owner
		// and anything else the forge reports without the proxy modelling it.
		try
		{
			if (JsonNode.Parse(body) is JsonObject parsed && parsed["lock"] is JsonNode held)
			{
				result["lock"] = held.DeepClone();
			}
		}
		catch (JsonException)
		{
			// A success with a body the proxy cannot read is still a success. Upstream said so.
		}

		return result;
	}

	private static string Describe(string body, HttpStatusCode status)
	{
		try
		{
			if (JsonNode.Parse(body) is JsonObject parsed
				&& JsonValues.String(parsed["message"]) is string message)
			{
				return message;
			}
		}
		catch (JsonException)
		{
			// Fall through to the status.
		}

		return status.ToString();
	}

	private static bool TryGetThrottle(HttpResponseMessage response, out TimeSpan retryAfter)
	{
		retryAfter = TimeSpan.Zero;

		// 403 as well as 429, because GitHub answers a secondary rate limit with 403 and a Retry-After
		// rather than with 429. Only when it carries the header: a plain 403 is a real refusal and
		// retrying it would be pointless.
		if (response.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden))
		{
			return false;
		}

		if (response.Headers.RetryAfter?.Delta is TimeSpan delta)
		{
			retryAfter = delta;
			return true;
		}

		if (response.Headers.TryGetValues("Retry-After", out IEnumerable<string>? values)
			&& int.TryParse(
				values.FirstOrDefault(),
				NumberStyles.None,
				CultureInfo.InvariantCulture,
				out int seconds))
		{
			retryAfter = TimeSpan.FromSeconds(seconds);
			return true;
		}

		return false;
	}
}
