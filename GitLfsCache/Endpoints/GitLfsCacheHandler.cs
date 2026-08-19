// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Endpoints;

using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using ktsu.GitLfsCache.Batch;
using ktsu.GitLfsCache.Configuration;
using ktsu.GitLfsCache.Fetching;
using ktsu.GitLfsCache.Locks;
using ktsu.GitLfsCache.Observability;
using ktsu.GitLfsCache.Storage;
using ktsu.GitLfsCache.Tokens;
using ktsu.GitLfsCache.Upstreams;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Handles every request under an upstream prefix.
/// </summary>
/// <remarks>
/// One handler rather than several because the paths share their front half: resolve the upstream,
/// validate the token, and decide whether the bytes come from the store or from upstream. Splitting
/// that across five endpoint classes would mean five copies of the same preamble.
/// </remarks>
/// <param name="registry">Resolves upstream keys.</param>
/// <param name="allowList">Decides which repository paths an upstream may be used for.</param>
/// <param name="upstreamClient">Sends requests upstream.</param>
/// <param name="codec">Decodes transfer tokens.</param>
/// <param name="rewriter">Rewrites batch responses.</param>
/// <param name="store">The local object store.</param>
/// <param name="coalescer">Keeps concurrent misses to one upstream fetch.</param>
/// <param name="lockLists">Answers lock listings from a snapshot.</param>
/// <param name="lockSnapshots">Holds lock snapshots, so a relayed change can invalidate one.</param>
/// <param name="publicUrls">Resolves the base URL rewritten hrefs point at.</param>
/// <param name="metrics">Cache counters.</param>
/// <param name="options">The configured options.</param>
/// <param name="logger">Logger.</param>
public sealed class GitLfsCacheHandler(
	IUpstreamRegistry registry,
	IRepositoryAllowList allowList,
	IUpstreamClient upstreamClient,
	IHrefTokenCodec codec,
	BatchRewriter rewriter,
	IObjectStore store,
	IFetchCoalescer coalescer,
	LockListService lockLists,
	ILockSnapshotStore lockSnapshots,
	PublicUrlResolver publicUrls,
	CacheMetrics metrics,
	IOptions<GitLfsCacheOptions> options,
	ILogger<GitLfsCacheHandler> logger)
{
	private const string OctetStream = "application/octet-stream";
	private const string TokenQueryParameter = "t";

	/// <summary>
	/// Dispatches one request.
	/// </summary>
	/// <param name="context">The request context.</param>
	/// <returns>A task that completes when the response has been written.</returns>
	public async Task HandleAsync(HttpContext context)
	{
		Ensure.NotNull(context);

		if (!LfsRouteParser.TryParse(context.Request.Path.Value, out LfsRoute? route))
		{
			context.Response.StatusCode = StatusCodes.Status404NotFound;
			return;
		}

		if (!registry.TryResolve(route.Upstream, out Uri? resolved) || resolved is null)
		{
			EndpointLog.UnknownUpstream(logger, route.Upstream);
			context.Response.StatusCode = StatusCodes.Status404NotFound;
			return;
		}

		// Checked here, once, rather than in each branch below, so batch, transfer, verify and relay
		// are all covered and any route added later inherits it. Before any upstream call, so a
		// refused path costs upstream nothing and cannot be used to learn anything about it. A 404
		// rather than a 403 for the same reason an unknown upstream key is a 404: separating the two
		// would tell a caller which repositories exist.
		if (!allowList.IsAllowed(route.Upstream, route.RelayPath))
		{
			EndpointLog.RepositoryNotAllowed(logger, route.RelayPath, route.Upstream);
			context.Response.StatusCode = StatusCodes.Status404NotFound;
			return;
		}

		Uri upstreamBase = resolved;

		CancellationToken cancellationToken = context.RequestAborted;

		switch (route.Kind)
		{
			case LfsRouteKind.Batch when HttpMethods.IsPost(context.Request.Method):
				await BatchAsync(context, route, upstreamBase, cancellationToken).ConfigureAwait(false);
				return;

			case LfsRouteKind.Transfer when HttpMethods.IsGet(context.Request.Method):
				await DownloadAsync(context, route, cancellationToken).ConfigureAwait(false);
				return;

			case LfsRouteKind.Transfer when HttpMethods.IsPut(context.Request.Method):
				await UploadAsync(context, route, cancellationToken).ConfigureAwait(false);
				return;

			case LfsRouteKind.Verify when HttpMethods.IsPost(context.Request.Method):
				await VerifyAsync(context, route, cancellationToken).ConfigureAwait(false);
				return;

			case LfsRouteKind.Locks when HttpMethods.IsGet(context.Request.Method):
				await LockListAsync(context, route, upstreamBase, cancellationToken).ConfigureAwait(false);
				return;

			// Creation and release are relayed, never terminated, because upstream is the only thing
			// that may grant or release a lock. The snapshot is dropped afterwards so the change this
			// client just made is visible to the next listing rather than waiting out the lifetime.
			case LfsRouteKind.Locks when HttpMethods.IsPost(context.Request.Method):
			case LfsRouteKind.LocksUnlock when HttpMethods.IsPost(context.Request.Method):
				await RelayAsync(context, route, upstreamBase, cancellationToken).ConfigureAwait(false);
				InvalidateLocksIfChanged(context, route);
				return;

			default:
				// Includes a recognized path reached with an unexpected method. Relaying rather than
				// rejecting keeps the proxy transparent to anything it does not model.
				await RelayAsync(context, route, upstreamBase, cancellationToken).ConfigureAwait(false);
				return;
		}
	}

	private async Task BatchAsync(
		HttpContext context,
		LfsRoute route,
		Uri upstreamBase,
		CancellationToken cancellationToken)
	{
		using HttpRequestMessage request = UpstreamRequests.BuildBatchRequest(
			upstreamBase,
			route.RepositoryPath,
			context.Request.Body,
			context.Request.Headers.Authorization.ToString());

		using HttpResponseMessage response = await upstreamClient
			.SendAsync(request, cancellationToken)
			.ConfigureAwait(false);

		// Upstream is the authority on access. A refusal is relayed exactly as it arrived, so the
		// client sees upstream's real answer rather than a proxy interpretation of it.
		if (!response.IsSuccessStatusCode)
		{
			await CopyResponseAsync(response, context, cancellationToken).ConfigureAwait(false);
			return;
		}

		JsonNode? upstreamBody;

		Stream batchBody = await response.Content
			.ReadAsStreamAsync(cancellationToken)
			.ConfigureAwait(false);

		await using (batchBody.ConfigureAwait(false))
		{
			upstreamBody = await JsonNode.ParseAsync(batchBody, cancellationToken: cancellationToken)
				.ConfigureAwait(false);
		}

		if (upstreamBody is null)
		{
			context.Response.StatusCode = StatusCodes.Status502BadGateway;
			return;
		}

		JsonNode rewritten = rewriter.Rewrite(upstreamBody, new BatchRewriteContext
		{
			Upstream = route.Upstream,
			RepositoryPath = route.RepositoryPath,
			PublicBaseUrl = publicUrls.Resolve(context.Request),
		});

		context.Response.StatusCode = StatusCodes.Status200OK;
		context.Response.ContentType = UpstreamRequests.LfsMediaType;
		await context.Response
			.WriteAsync(rewritten.ToJsonString(), cancellationToken)
			.ConfigureAwait(false);
	}

	private async Task DownloadAsync(HttpContext context, LfsRoute route, CancellationToken cancellationToken)
	{
		if (!TryGetToken(context, route, TokenAction.Download, out HrefToken? token))
		{
			return;
		}

		string range = context.Request.Headers.Range.ToString();

		Stream? cached = store.OpenRead(route.Upstream, token.Oid, out long length);

		if (cached is not null)
		{
			await using (cached.ConfigureAwait(false))
			{
				store.Touch(route.Upstream, token.Oid);
				metrics.RecordHit(route.Upstream, length);
				EndpointLog.ServedFromCache(logger, token.Oid, route.Upstream);

				await ServeFromStoreAsync(context, cached, length, cancellationToken).ConfigureAwait(false);
			}

			return;
		}

		if (!string.IsNullOrEmpty(range))
		{
			// A partial response cannot be stored as a whole object, so the range is forwarded and the
			// result streamed straight through. Rare enough not to be worth partial-object bookkeeping.
			EndpointLog.RangeRequestNotStored(logger, token.Oid);
			await StreamFromUpstreamAsync(context, route, token, range, storeLocally: false, cancellationToken)
				.ConfigureAwait(false);
			return;
		}

		using IFetchTicket ticket = coalescer.Acquire(route.Upstream, token.Oid);

		if (!ticket.IsLeader)
		{
			metrics.RecordCoalescedWait(route.Upstream);
			EndpointLog.WaitingForLeader(logger, token.Oid, route.Upstream);

			bool published = await ticket
				.WaitForLeaderAsync(options.Value.Fetch.FollowerTimeout, cancellationToken)
				.ConfigureAwait(false);

			long nowLength = 0;
			Stream? nowCached = published
				? store.OpenRead(route.Upstream, token.Oid, out nowLength)
				: null;

			if (nowCached is not null)
			{
				await using (nowCached.ConfigureAwait(false))
				{
					store.Touch(route.Upstream, token.Oid);
					metrics.RecordHit(route.Upstream, nowLength);
					await ServeFromStoreAsync(context, nowCached, nowLength, cancellationToken)
						.ConfigureAwait(false);
				}

				return;
			}

			EndpointLog.LeaderDidNotFinish(logger, token.Oid);
		}

		bool stored = await StreamFromUpstreamAsync(
			context,
			route,
			token,
			range: null,
			storeLocally: true,
			cancellationToken).ConfigureAwait(false);

		if (ticket.IsLeader)
		{
			ticket.Complete(stored);
		}
	}

	private async Task<bool> StreamFromUpstreamAsync(
		HttpContext context,
		LfsRoute route,
		HrefToken token,
		string? range,
		bool storeLocally,
		CancellationToken cancellationToken)
	{
		EndpointLog.FetchingUpstream(logger, token.Oid, route.Upstream);

		using HttpRequestMessage request = UpstreamRequests.BuildObjectRequest(token, range);
		using HttpResponseMessage response = await upstreamClient
			.SendAsync(request, cancellationToken)
			.ConfigureAwait(false);

		if (!response.IsSuccessStatusCode)
		{
			EndpointLog.UpstreamRefusedTransfer(logger, (int)response.StatusCode, token.Oid);
			await CopyResponseAsync(response, context, cancellationToken).ConfigureAwait(false);
			return false;
		}

		CopyTransferHeaders(response, context);

		Stream upstreamBody = await response.Content
			.ReadAsStreamAsync(cancellationToken)
			.ConfigureAwait(false);

		await using ConfiguredAsyncDisposable upstreamBodyDisposal = upstreamBody.ConfigureAwait(false);

		if (!storeLocally)
		{
			long streamed = await StreamTee
				.CopyAsync(upstreamBody, context.Response.Body, null, null, cancellationToken)
				.ConfigureAwait(false);

			metrics.RecordMiss(route.Upstream, streamed);
			return false;
		}

		StagingHandle staging = store.OpenStaging(route.Upstream);

		await using (staging.ConfigureAwait(false))
		{
			long streamed = await StreamTee.CopyAsync(
				upstreamBody,
				context.Response.Body,
				staging.Stream,
				failure => EndpointLog.StoreSinkFailed(logger, failure, token.Oid),
				cancellationToken).ConfigureAwait(false);

			metrics.RecordMiss(route.Upstream, streamed);

			bool published = await store
				.PublishAsync(staging, route.Upstream, token.Oid, cancellationToken)
				.ConfigureAwait(false);

			if (published)
			{
				metrics.RecordStored(route.Upstream);
			}
			else
			{
				metrics.RecordVerificationFailure(route.Upstream);
			}

			return published;
		}
	}

	private async Task UploadAsync(HttpContext context, LfsRoute route, CancellationToken cancellationToken)
	{
		if (!TryGetToken(context, route, TokenAction.Upload, out HrefToken? token))
		{
			return;
		}

		StagingHandle staging = store.OpenStaging(route.Upstream);

		await using (staging.ConfigureAwait(false))
		{
			ReadTeeStream teed = new(
				context.Request.Body,
				staging.Stream,
				failure => EndpointLog.StoreSinkFailed(logger, failure, token.Oid));

			await using ConfiguredAsyncDisposable teedDisposal = teed.ConfigureAwait(false);

			using HttpRequestMessage request = UpstreamRequests.BuildUploadRequest(
				token,
				teed,
				context.Request.ContentLength);

			using HttpResponseMessage response = await upstreamClient
				.SendAsync(request, cancellationToken)
				.ConfigureAwait(false);

			metrics.RecordUpload(route.Upstream, teed.BytesRead);

			// The object is published only after upstream accepts it. Caching an upload upstream
			// rejected would serve bytes no one can verify against the real remote.
			if (response.IsSuccessStatusCode && teed.SinkIsLive)
			{
				if (await store.PublishAsync(staging, route.Upstream, token.Oid, cancellationToken)
					.ConfigureAwait(false))
				{
					metrics.RecordStored(route.Upstream);
				}
				else
				{
					metrics.RecordVerificationFailure(route.Upstream);
				}
			}

			await CopyResponseAsync(response, context, cancellationToken).ConfigureAwait(false);
		}
	}

	private async Task VerifyAsync(HttpContext context, LfsRoute route, CancellationToken cancellationToken)
	{
		if (!TryGetToken(context, route, TokenAction.Verify, out HrefToken? token))
		{
			return;
		}

		using HttpRequestMessage request = UpstreamRequests.BuildVerifyRequest(token, context.Request.Body);
		using HttpResponseMessage response = await upstreamClient
			.SendAsync(request, cancellationToken)
			.ConfigureAwait(false);

		await CopyResponseAsync(response, context, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Answers a lock listing, from the snapshot when that is both possible and permitted.
	/// </summary>
	private async Task LockListAsync(
		HttpContext context,
		LfsRoute route,
		Uri upstreamBase,
		CancellationToken cancellationToken)
	{
		LockSnapshotKey key = new(
			route.Upstream,
			route.RepositoryPath,
			context.Request.Query["refspec"].FirstOrDefault());

		LockListOutcome outcome = await lockLists
			.ResolveAsync(key, upstreamBase, context.Request.Headers.Authorization.ToString(), cancellationToken)
			.ConfigureAwait(false);

		switch (outcome.Kind)
		{
			case LockListOutcomeKind.Refuse:
				// Upstream's own refusal, not a proxy interpretation of it.
				context.Response.StatusCode = (int)outcome.Status!.Value;
				return;

			case LockListOutcomeKind.Serve:
				await WriteLockPageAsync(context, outcome.Snapshot!, cancellationToken).ConfigureAwait(false);
				return;

			default:
				await RelayAsync(context, route, upstreamBase, cancellationToken).ConfigureAwait(false);
				return;
		}
	}

	/// <summary>
	/// Writes one page of a snapshot, applying the filters and cursor the client asked for.
	/// </summary>
	private static async Task WriteLockPageAsync(
		HttpContext context,
		LockSnapshot snapshot,
		CancellationToken cancellationToken)
	{
		IReadOnlyList<LockEntry> matches = snapshot.Filter(
			context.Request.Query["path"].FirstOrDefault(),
			context.Request.Query["id"].FirstOrDefault());

		int offset = 0;

		// A cursor from a snapshot that has since been replaced restarts the walk rather than being
		// applied to a different ordering, which would silently skip or repeat locks.
		if (LockCursor.TryDecode(context.Request.Query["cursor"].FirstOrDefault(), out LockCursor? cursor)
			&& cursor.SnapshotId == snapshot.Id)
		{
			offset = cursor.Offset;
		}

		int? limit = int.TryParse(
			context.Request.Query["limit"].FirstOrDefault(),
			System.Globalization.NumberStyles.None,
			System.Globalization.CultureInfo.InvariantCulture,
			out int requested)
			? requested
			: null;

		(IReadOnlyList<LockEntry> page, int? nextOffset) = LockSnapshot.Paginate(matches, offset, limit);

		JsonObject body = LockListParser.BuildResponse(
			page,
			nextOffset is int next ? new LockCursor(snapshot.Id, next).Encode() : null);

		context.Response.StatusCode = StatusCodes.Status200OK;
		context.Response.ContentType = UpstreamRequests.LfsMediaType;

		await context.Response
			.WriteAsync(body.ToJsonString(), cancellationToken)
			.ConfigureAwait(false);
	}

	/// <summary>
	/// Drops the snapshot when a relayed lock change actually took effect.
	/// </summary>
	/// <remarks>
	/// Gated on the response status, because invalidating after a refused creation would throw away a
	/// perfectly good snapshot every time two people raced for the same file, which is exactly when
	/// the cache is under the most load.
	/// </remarks>
	private void InvalidateLocksIfChanged(HttpContext context, LfsRoute route)
	{
		if (context.Response.StatusCode is >= 200 and < 300)
		{
			lockSnapshots.Invalidate(new LockSnapshotKey(
				route.Upstream,
				route.RepositoryPath,
				context.Request.Query["refspec"].FirstOrDefault()));
		}
	}

	private async Task RelayAsync(
		HttpContext context,
		LfsRoute route,
		Uri upstreamBase,
		CancellationToken cancellationToken)
	{
		IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers = context.Request.Headers
			.Select(header => new KeyValuePair<string, IEnumerable<string>>(
				header.Key,
				header.Value.Where(value => value is not null).Select(value => value!)));

		using HttpRequestMessage request = UpstreamRequests.BuildRelayRequest(
			upstreamBase,
			context.Request.Method,
			route.RelayPath,
			context.Request.QueryString.Value ?? string.Empty,
			context.Request.Body,
			headers);

		using HttpResponseMessage response = await upstreamClient
			.SendAsync(request, cancellationToken)
			.ConfigureAwait(false);

		EndpointLog.Relayed(logger, context.Request.Method, route.RelayPath, route.Upstream);
		await CopyResponseAsync(response, context, cancellationToken).ConfigureAwait(false);
	}

	private bool TryGetToken(
		HttpContext context,
		LfsRoute route,
		string expectedAction,
		[NotNullWhen(true)] out HrefToken? token)
	{
		token = null;
		string? encoded = context.Request.Query[TokenQueryParameter];

		if (!codec.TryDecode(encoded, out HrefToken? decoded, out string? failureReason))
		{
			metrics.RecordRejectedToken();
			EndpointLog.RejectedToken(logger, route.Oid ?? "(none)", failureReason ?? "unspecified");

			// No detail in the response: telling a caller which part of a token it got wrong only
			// helps a caller who is guessing.
			context.Response.StatusCode = StatusCodes.Status403Forbidden;
			return false;
		}

		if (decoded.Action != expectedAction)
		{
			metrics.RecordRejectedToken();
			EndpointLog.TokenActionMismatch(logger, decoded.Action, expectedAction);
			context.Response.StatusCode = StatusCodes.Status403Forbidden;
			return false;
		}

		// A token is bound to one object, so it cannot be replayed against a different path.
		if (decoded.Oid != route.Oid || decoded.Upstream != route.Upstream)
		{
			metrics.RecordRejectedToken();
			EndpointLog.RejectedToken(logger, route.Oid ?? "(none)", "token does not match the requested object");
			context.Response.StatusCode = StatusCodes.Status403Forbidden;
			return false;
		}

		token = decoded;
		return true;
	}

	private static async Task ServeFromStoreAsync(
		HttpContext context,
		Stream cached,
		long length,
		CancellationToken cancellationToken)
	{
		context.Response.ContentType = OctetStream;
		context.Response.ContentLength = length;

		await StreamTee
			.CopyAsync(cached, context.Response.Body, null, null, cancellationToken)
			.ConfigureAwait(false);
	}

	private static void CopyTransferHeaders(HttpResponseMessage response, HttpContext context)
	{
		context.Response.StatusCode = (int)response.StatusCode;
		context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? OctetStream;

		if (response.Content.Headers.ContentLength is long length)
		{
			context.Response.ContentLength = length;
		}

		if (response.Content.Headers.ContentRange is not null)
		{
			context.Response.Headers.ContentRange = response.Content.Headers.ContentRange.ToString();
		}

		if (response.Headers.AcceptRanges.Count > 0)
		{
			context.Response.Headers.AcceptRanges = string.Join(", ", response.Headers.AcceptRanges);
		}
	}

	private static async Task CopyResponseAsync(
		HttpResponseMessage response,
		HttpContext context,
		CancellationToken cancellationToken)
	{
		context.Response.StatusCode = (int)response.StatusCode;

		foreach ((string name, IEnumerable<string> values) in response.Headers)
		{
			if (!UpstreamRequests.IsHopHeader(name))
			{
				context.Response.Headers[name] = values.ToArray();
			}
		}

		foreach ((string name, IEnumerable<string> values) in response.Content.Headers)
		{
			if (!UpstreamRequests.IsHopHeader(name))
			{
				context.Response.Headers[name] = values.ToArray();
			}
		}

		if (response.StatusCode == HttpStatusCode.NoContent)
		{
			return;
		}

		Stream body = await response.Content
			.ReadAsStreamAsync(cancellationToken)
			.ConfigureAwait(false);

		await using (body.ConfigureAwait(false))
		{
			await body.CopyToAsync(context.Response.Body, cancellationToken).ConfigureAwait(false);
		}
	}
}
