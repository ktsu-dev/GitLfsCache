// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Locks;

/// <summary>
/// Holds at most one published lock snapshot per repository.
/// </summary>
/// <remarks>
/// In memory and never persisted. A restart costs one refresh, which is cheaper than reasoning about
/// a persisted cache of advisory state that could come back wrong.
/// </remarks>
public interface ILockSnapshotStore
{
	/// <summary>
	/// Reads the current snapshot for a repository.
	/// </summary>
	/// <param name="key">The repository the snapshot belongs to.</param>
	/// <returns>The snapshot, or null when none has been published or it was invalidated.</returns>
	public LockSnapshot? Read(LockSnapshotKey key);

	/// <summary>
	/// Publishes a snapshot, replacing any previous one for the same repository.
	/// </summary>
	/// <param name="key">The repository the snapshot belongs to.</param>
	/// <param name="snapshot">The snapshot.</param>
	public void Publish(LockSnapshotKey key, LockSnapshot snapshot);

	/// <summary>
	/// Drops every snapshot for a repository, whatever ref it was listed under, so the next read of
	/// any of them refreshes.
	/// </summary>
	/// <remarks>
	/// Called after a lock creation or release the proxy relayed successfully. Locks changed outside
	/// the proxy are not seen here and are bounded only by the listing lifetime.
	/// <para>
	/// Every ref goes, not only the one the change named. A listing carries its ref in the query
	/// string while a create or unlock carries it in the body, and a client can list under one ref
	/// and lock under another, so the change cannot reliably name the snapshot it made wrong. Dropping
	/// the rest only costs a refetch.
	/// </para>
	/// </remarks>
	/// <param name="upstream">The upstream the repository is served from.</param>
	/// <param name="repositoryPath">The repository whose snapshots are now known to be wrong.</param>
	public void Invalidate(string upstream, string repositoryPath);
}
