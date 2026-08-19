// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Locks;

/// <summary>
/// Walks a repository's lock listing upstream.
/// </summary>
public interface ILockListRefresher
{
	/// <summary>
	/// Asks upstream whether a credential may read this repository's locks, without walking them.
	/// </summary>
	/// <remarks>
	/// Used when a snapshot is already fresh but the caller has no admission. A full walk would prove
	/// the same thing at many times the cost.
	/// </remarks>
	/// <param name="key">The repository to ask about.</param>
	/// <param name="upstreamBase">The configured upstream base URL.</param>
	/// <param name="authorization">The requesting client's Authorization header.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>
	/// A succeeded result when upstream accepted the credential, carrying no useful snapshot, or a
	/// refusal carrying upstream's status.
	/// </returns>
	public Task<LockRefreshResult> ProbeAsync(
		LockSnapshotKey key,
		Uri upstreamBase,
		string? authorization,
		CancellationToken cancellationToken);

	/// <summary>
	/// Walks every page of a repository's locks.
	/// </summary>
	/// <param name="key">The repository to walk.</param>
	/// <param name="upstreamBase">The configured upstream base URL.</param>
	/// <param name="authorization">The requesting client's Authorization header.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The outcome of the walk.</returns>
	public Task<LockRefreshResult> RefreshAsync(
		LockSnapshotKey key,
		Uri upstreamBase,
		string? authorization,
		CancellationToken cancellationToken);
}
