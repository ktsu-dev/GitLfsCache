// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Storage;

/// <summary>
/// What one eviction sweep accomplished.
/// </summary>
/// <param name="EvictedCount">How many objects were deleted.</param>
/// <param name="EvictedBytes">How many bytes those objects held.</param>
/// <param name="SkippedCount">
/// How many objects could not be deleted, because a live transfer held them open. Those are retried
/// on the next sweep rather than failing the request that holds them.
/// </param>
/// <param name="TargetBytes">The size the sweep was aiming to get under.</param>
public sealed record EvictionResult(
	int EvictedCount,
	long EvictedBytes,
	int SkippedCount,
	long TargetBytes)
{
	/// <summary>A sweep that had nothing to do.</summary>
	/// <param name="targetBytes">The size the store was already under.</param>
	/// <returns>An empty result.</returns>
	public static EvictionResult NothingToDo(long targetBytes) => new(0, 0, 0, targetBytes);
}
