// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Locks;

/// <summary>
/// Every lock a repository held at one instant.
/// </summary>
/// <remarks>
/// Immutable, and published by replacement rather than mutated in place. That is what lets a client
/// walk pages without seeing a torn view: relaying cursors to upstream today means locks can appear
/// and disappear part way through a walk, because each page is a separate query against a moving
/// collection. Paging a snapshot cannot do that.
/// </remarks>
/// <param name="locks">The locks, in the order upstream returned them.</param>
/// <param name="takenAt">When the walk that produced this completed.</param>
public sealed class LockSnapshot(IReadOnlyList<LockEntry> locks, DateTimeOffset takenAt)
{
	/// <summary>
	/// Gets this snapshot's identity, which a cursor carries so it cannot be applied to a later one.
	/// </summary>
	/// <remarks>
	/// Fresh per instance rather than derived from the contents. Two snapshots holding identical locks
	/// are still separate walks, and a cursor minted against one is only known to be an offset into
	/// the ordering that walk produced.
	/// </remarks>
	public Guid Id { get; } = Guid.NewGuid();

	/// <summary>Gets the locks, in the order upstream returned them.</summary>
	public IReadOnlyList<LockEntry> Locks { get; } = locks;

	/// <summary>Gets when the walk that produced this snapshot completed.</summary>
	public DateTimeOffset TakenAt { get; } = takenAt;

	/// <summary>
	/// Reports whether this snapshot is older than a maximum age at a given instant.
	/// </summary>
	/// <param name="now">The current instant.</param>
	/// <param name="maximumAge">How old a snapshot may be and still be served.</param>
	/// <returns><see langword="true"/> when the snapshot must be refreshed before use.</returns>
	public bool IsStale(DateTimeOffset now, TimeSpan maximumAge) => now - TakenAt >= maximumAge;

	/// <summary>
	/// Applies the filters the locking API defines for a listing.
	/// </summary>
	/// <remarks>
	/// Both filters are exact matches upstream, so applying them here reproduces upstream's answer
	/// rather than approximating it, and the unfiltered snapshot is a superset of any filtered result
	/// by construction. That is what allows a filtered query to be answered locally at all, and it is
	/// what makes the client's own threshold for asking about individual files pointless once the
	/// listing is cached.
	/// <para>
	/// Path comparison is ordinal. Git paths are case sensitive, and two entries differing only by
	/// case are two different files.
	/// </para>
	/// </remarks>
	/// <param name="path">An exact path to filter by, or null for no path filter.</param>
	/// <param name="id">An exact lock id to filter by, or null for no id filter.</param>
	/// <returns>The matching locks, in snapshot order.</returns>
	public IReadOnlyList<LockEntry> Filter(string? path, string? id)
	{
		if (path is null && id is null)
		{
			return Locks;
		}

		List<LockEntry> matches = [];

		foreach (LockEntry entry in Locks)
		{
			bool pathMatches = path is null || string.Equals(entry.Path, path, StringComparison.Ordinal);
			bool idMatches = id is null || string.Equals(entry.Id, id, StringComparison.Ordinal);

			if (pathMatches && idMatches)
			{
				matches.Add(entry);
			}
		}

		return matches;
	}

	/// <summary>
	/// Takes one page out of a filtered result.
	/// </summary>
	/// <remarks>
	/// The offset is a position within this snapshot, so a cursor is only meaningful alongside the
	/// snapshot that produced it. The caller is responsible for noticing that a cursor belongs to a
	/// snapshot which has since been replaced; this method only slices.
	/// </remarks>
	/// <param name="matches">The filtered locks.</param>
	/// <param name="offset">How many to skip.</param>
	/// <param name="limit">The most to return, or null for all of them.</param>
	/// <returns>The page, and the offset the next page starts at, or null when this is the last.</returns>
	public static (IReadOnlyList<LockEntry> Page, int? NextOffset) Paginate(
		IReadOnlyList<LockEntry> matches,
		int offset,
		int? limit)
	{
		Ensure.NotNull(matches);

		int start = Math.Clamp(offset, 0, matches.Count);
		int count = limit is int requested && requested >= 0
			? Math.Min(requested, matches.Count - start)
			: matches.Count - start;

		List<LockEntry> page = [];

		for (int index = start; index < start + count; index++)
		{
			page.Add(matches[index]);
		}

		int next = start + count;

		// A limit of zero would otherwise report a next offset equal to the current one, which is a
		// cursor that never advances and a client that never finishes.
		return (page, next < matches.Count && count > 0 ? next : null);
	}
}
