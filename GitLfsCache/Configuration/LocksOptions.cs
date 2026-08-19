// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Configuration;

/// <summary>
/// Settings for the lock listing cache and batched locking.
/// </summary>
public sealed class LocksOptions
{
	/// <summary>
	/// Gets or sets a value indicating whether the proxy terminates any part of the locking API.
	/// </summary>
	/// <remarks>
	/// When false every lock route is relayed, which is what the proxy did before this subsystem
	/// existed and remains the safe fallback if the cache is ever suspected of being wrong.
	/// </remarks>
	public bool Enabled { get; set; } = true;

	/// <summary>
	/// Gets or sets how long a lock listing may be served before it is refreshed.
	/// </summary>
	/// <remarks>
	/// Short deliberately. Locks taken outside the proxy, through a forge's web interface or a client
	/// not configured through it, are invisible here and this is the only thing bounding how long they
	/// stay invisible. Staleness costs a client a refused lock attempt and a retry rather than a
	/// conflict, because upstream still decides every creation, but that argument gets weaker the
	/// longer this value gets.
	/// </remarks>
	public TimeSpan ListTtl { get; set; } = TimeSpan.FromSeconds(15);

	/// <summary>
	/// Gets or sets how long an upstream authorization is trusted before it is proven again.
	/// </summary>
	/// <remarks>
	/// This is the window in which a credential revoked upstream can still read lock listings. It
	/// grants nothing else, and never object bytes.
	/// </remarks>
	public TimeSpan AdmissionTtl { get; set; } = TimeSpan.FromMinutes(1);

	/// <summary>
	/// Gets or sets how long a request waits for another request's refresh before refreshing itself.
	/// </summary>
	public TimeSpan RefreshTimeout { get; set; } = TimeSpan.FromSeconds(30);

	/// <summary>
	/// Gets or sets the most locks a snapshot may hold before the repository falls back to relaying.
	/// </summary>
	/// <remarks>
	/// An unbounded in-memory listing is a worse failure than a slow client, so a repository whose
	/// lock count exceeds this is served by relaying rather than by caching.
	/// </remarks>
	public int MaxSnapshotLocks { get; set; } = 100_000;

	/// <summary>
	/// Gets or sets how many lock calls may be in flight against one upstream at a time.
	/// </summary>
	/// <remarks>
	/// Per upstream and shared across every in-flight request in the process, not per request. Two
	/// clients each batching five hundred paths would otherwise collectively exceed whatever the forge
	/// tolerates while each stayed within its own limit.
	/// <para>
	/// The right value for GitHub and for Azure DevOps is not known in advance and has to be found by
	/// measurement. This default is deliberately cautious.
	/// </para>
	/// </remarks>
	public int MaxFanOutConcurrency { get; set; } = 8;

	/// <summary>
	/// Gets or sets the most paths one batched lock request may carry.
	/// </summary>
	/// <remarks>
	/// Refusing an oversized request outright is better than accepting work that will certainly be
	/// throttled part way through and leave the caller reconciling a partial result.
	/// </remarks>
	public int MaxFanOutPaths { get; set; } = 1000;

	/// <summary>
	/// Gets or sets how many times a throttled lock call is retried before it is reported as failed.
	/// </summary>
	public int MaxFanOutRetries { get; set; } = 3;
}
