// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Locks;

using System.Text.Json.Nodes;
using ktsu.GitLfsCache.Configuration;
using ktsu.GitLfsCache.Upstreams;
using Microsoft.Extensions.Options;

/// <summary>
/// Walks a repository's lock listing upstream and assembles a snapshot.
/// </summary>
/// <remarks>
/// Performed with the requesting client's own credential, never a credential the proxy holds. That is
/// what allows this to exist at all without the proxy becoming an authority: a walk that succeeds is
/// itself proof the caller was permitted to read these locks, and a walk that fails is upstream's
/// answer to relay rather than something to work around.
/// <para>
/// A background refresher on a timer would be the obvious design and is deliberately not used,
/// because it would need a credential of its own.
/// </para>
/// </remarks>
/// <param name="upstreamClient">Sends requests upstream.</param>
/// <param name="options">The configured options.</param>
/// <param name="timeProvider">Clock, injected so snapshot ages are testable.</param>
public sealed class LockListRefresher(
	IUpstreamClient upstreamClient,
	IOptions<GitLfsCacheOptions> options,
	TimeProvider timeProvider) : ILockListRefresher
{
	/// <summary>
	/// How many pages a walk may request before giving up.
	/// </summary>
	/// <remarks>
	/// A backstop against an upstream that returns a cursor forever. The lock ceiling is enforced
	/// separately and precisely; this only stops a pathological server from holding a request open
	/// indefinitely.
	/// </remarks>
	private const int MaximumPages = 10_000;

	/// <inheritdoc />
	public async Task<LockRefreshResult> ProbeAsync(
		LockSnapshotKey key,
		Uri upstreamBase,
		string? authorization,
		CancellationToken cancellationToken)
	{
		Ensure.NotNull(key);

		// One page, and the smallest one upstream will give. The body is irrelevant: the only question
		// is whether upstream accepts this credential for this repository, and the status answers it.
		using HttpRequestMessage request = UpstreamRequests.BuildLockListRequest(
			upstreamBase,
			key.RepositoryPath,
			cursor: null,
			limit: 1,
			authorization);

		using HttpResponseMessage response = await upstreamClient
			.SendAsync(request, cancellationToken)
			.ConfigureAwait(false);

		return response.IsSuccessStatusCode
			? LockRefreshResult.Succeeded(new LockSnapshot([], timeProvider.GetUtcNow()))
			: LockRefreshResult.Refused(response.StatusCode);
	}

	/// <inheritdoc />
	public async Task<LockRefreshResult> RefreshAsync(
		LockSnapshotKey key,
		Uri upstreamBase,
		string? authorization,
		CancellationToken cancellationToken)
	{
		Ensure.NotNull(key);

		List<LockEntry> collected = [];
		string? cursor = null;
		int maximumLocks = options.Value.Locks.MaxSnapshotLocks;

		for (int page = 0; page < MaximumPages; page++)
		{
			using HttpRequestMessage request = UpstreamRequests.BuildLockListRequest(
				upstreamBase,
				key.RepositoryPath,
				cursor,
				limit: null,
				authorization);

			using HttpResponseMessage response = await upstreamClient
				.SendAsync(request, cancellationToken)
				.ConfigureAwait(false);

			if (!response.IsSuccessStatusCode)
			{
				return LockRefreshResult.Refused(response.StatusCode);
			}

			string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			JsonNode? parsed;

			try
			{
				parsed = JsonNode.Parse(body);
			}
			catch (System.Text.Json.JsonException)
			{
				return LockRefreshResult.Unusable();
			}

			if (!LockListParser.TryParsePage(parsed, out IReadOnlyList<LockEntry>? entries, out string? next))
			{
				return LockRefreshResult.Unusable();
			}

			collected.AddRange(entries);

			// Checked while walking rather than at the end, so a repository far over the ceiling stops
			// costing memory as soon as it is known to be over it.
			if (collected.Count > maximumLocks)
			{
				return LockRefreshResult.TooLarge(collected.Count);
			}

			if (next is null)
			{
				return LockRefreshResult.Succeeded(new LockSnapshot(collected, timeProvider.GetUtcNow()));
			}

			// An upstream repeating its cursor would otherwise be an endless walk that never trips the
			// page ceiling for a small repository.
			if (string.Equals(next, cursor, StringComparison.Ordinal))
			{
				return LockRefreshResult.Unusable();
			}

			cursor = next;
		}

		return LockRefreshResult.Unusable();
	}
}
