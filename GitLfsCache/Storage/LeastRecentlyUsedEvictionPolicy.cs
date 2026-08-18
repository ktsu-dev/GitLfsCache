// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Storage;

using ktsu.GitLfsCache.Configuration;
using Microsoft.Extensions.Options;

/// <summary>
/// Deletes the coldest objects first until the store is under its byte budget.
/// </summary>
/// <remarks>
/// A sweep reduces the store to a low-water mark rather than to exactly the budget, so it does not
/// thrash at the boundary: without that margin, every new object past the limit would trigger another
/// sweep that deletes exactly one object.
/// <para>
/// Coldness is read from the access times the store stamps on every hit. An object a live transfer
/// holds open cannot be deleted on Windows, so it is counted as skipped and retried next sweep.
/// </para>
/// </remarks>
/// <param name="store">The store to sweep.</param>
/// <param name="options">The configured options, supplying the budget and low-water mark.</param>
public sealed class LeastRecentlyUsedEvictionPolicy(
	IObjectStore store,
	IOptions<GitLfsCacheOptions> options) : IEvictionPolicy
{
	/// <inheritdoc />
	public EvictionResult Evict()
	{
		StoreOptions storeOptions = options.Value.Store;
		long budget = storeOptions.MaxSizeBytes;
		long target = (long)(budget * storeOptions.LowWaterMark);

		if (store.TotalBytes <= budget)
		{
			return EvictionResult.NothingToDo(target);
		}

		// Materialized before deleting, because deleting while enumerating the filesystem is not
		// something the abstraction promises to tolerate.
		List<StoredObject> coldestFirst = [.. store.Enumerate().OrderBy(stored => stored.LastAccessUtc)];

		int evicted = 0;
		int skipped = 0;
		long freed = 0;

		foreach (StoredObject candidate in coldestFirst)
		{
			if (store.TotalBytes <= target)
			{
				break;
			}

			if (store.TryDelete(candidate))
			{
				evicted++;
				freed += candidate.Size;
			}
			else
			{
				skipped++;
			}
		}

		return new EvictionResult(evicted, freed, skipped, target);
	}
}
