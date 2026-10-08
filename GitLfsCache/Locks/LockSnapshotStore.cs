// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Locks;

using System.Collections.Concurrent;
using ktsu.GitLfsCache.Configuration;
using Microsoft.Extensions.Options;

/// <summary>
/// In-memory snapshot store, one entry per repository and ref, replaced on publish.
/// </summary>
/// <remarks>
/// Replacement rather than mutation is what makes a snapshot safe to hand to many concurrent readers
/// without a lock: a reader holds the instance it took, and a publish landing underneath it changes
/// nothing that reader can see.
/// <para>
/// The ref in the key comes straight from the client's query string, so the number of keys is not
/// bounded by anything the operator controls. Every publish therefore drops snapshots older than
/// <see cref="LocksOptions.ListTtl"/>, which would be refetched before being served anyway, and then
/// drops the oldest survivors until at most <see cref="LocksOptions.MaxSnapshots"/> remain.
/// Eviction happens on publish rather than on a timer because a publish is the only thing that
/// grows the store, and it follows a full upstream walk, so a scan of the store is noise beside it.
/// </para>
/// </remarks>
/// <param name="options">The configured options.</param>
/// <param name="timeProvider">Clock, injected so expiry is testable.</param>
public sealed class LockSnapshotStore(
	IOptions<GitLfsCacheOptions> options,
	TimeProvider timeProvider) : ILockSnapshotStore
{
	private readonly ConcurrentDictionary<LockSnapshotKey, LockSnapshot> _snapshots = new();

	// Serializes eviction only. Reads never take it, and publishes are rare next to reads.
	private readonly Lock _evictionGate = new();

	/// <summary>Gets how many snapshots the store currently holds.</summary>
	public int Count => _snapshots.Count;

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

		Evict(key);
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

	private void Evict(LockSnapshotKey published)
	{
		LocksOptions locks = options.Value.Locks;
		DateTimeOffset now = timeProvider.GetUtcNow();

		lock (_evictionGate)
		{
			List<KeyValuePair<LockSnapshotKey, LockSnapshot>> survivors = [];

			foreach (KeyValuePair<LockSnapshotKey, LockSnapshot> entry in _snapshots)
			{
				// The snapshot just published is never a candidate, even when its walk took long
				// enough to make it look stale or old: evicting it would throw away the work that
				// triggered this call.
				if (entry.Key.Equals(published))
				{
					continue;
				}

				// Removal is conditional on the value, so a snapshot republished under the same key
				// since the scan began is left alone.
				if (entry.Value.IsStale(now, locks.ListTtl))
				{
					_snapshots.TryRemove(entry);
				}
				else
				{
					survivors.Add(entry);
				}
			}

			// One slot is the snapshot just published.
			int excess = survivors.Count + 1 - locks.MaxSnapshots;

			if (excess <= 0)
			{
				return;
			}

			foreach (KeyValuePair<LockSnapshotKey, LockSnapshot> entry in survivors
				.OrderBy(entry => entry.Value.TakenAt)
				.Take(excess))
			{
				_snapshots.TryRemove(entry);
			}
		}
	}
}
