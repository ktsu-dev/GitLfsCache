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
	/// Drops the snapshot for a repository, so the next read refreshes.
	/// </summary>
	/// <remarks>
	/// Called after a lock creation or release the proxy relayed successfully. Locks changed outside
	/// the proxy are not seen here and are bounded only by the listing lifetime.
	/// </remarks>
	/// <param name="key">The repository whose snapshot is now known to be wrong.</param>
	public void Invalidate(LockSnapshotKey key);
}
