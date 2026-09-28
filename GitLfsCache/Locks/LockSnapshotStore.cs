// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Locks;

using System.Collections.Concurrent;

/// <summary>
/// In-memory snapshot store, one entry per repository, replaced on publish.
/// </summary>
/// <remarks>
/// Replacement rather than mutation is what makes a snapshot safe to hand to many concurrent readers
/// without a lock: a reader holds the instance it took, and a publish landing underneath it changes
/// nothing that reader can see.
/// </remarks>
public sealed class LockSnapshotStore : ILockSnapshotStore
{
	private readonly ConcurrentDictionary<LockSnapshotKey, LockSnapshot> _snapshots = new();

	/// <inheritdoc />
	public LockSnapshot? Read(LockSnapshotKey key)
	{
		Ensure.NotNull(key);
		return _snapshots.TryGetValue(key, out LockSnapshot? snapshot) ? snapshot : null;
	}

	/// <inheritdoc />
	public void Publish(LockSnapshotKey key, LockSnapshot snapshot)
	{
		Ensure.NotNull(key);
		Ensure.NotNull(snapshot);

		_snapshots[key] = snapshot;
	}

	/// <inheritdoc />
	public void Invalidate(string upstream, string repositoryPath)
	{
		Ensure.NotNull(upstream);
		Ensure.NotNull(repositoryPath);

		foreach (LockSnapshotKey key in _snapshots.Keys.Where(key =>
			string.Equals(key.Upstream, upstream, StringComparison.Ordinal)
			&& string.Equals(key.RepositoryPath, repositoryPath, StringComparison.Ordinal)))
		{
			_snapshots.TryRemove(key, out _);
		}
	}
}
