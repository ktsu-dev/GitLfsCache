// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Locks;

using System.Text.Json.Nodes;
using ktsu.GitLfsCache.Locks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class LockSnapshotTests
{
	private static readonly DateTimeOffset TakenAt = new(2026, 8, 19, 9, 47, 0, TimeSpan.Zero);

	private static readonly string[] EveryIdInOrder = ["1", "2", "3", "4", "5"];

	private static LockEntry Entry(string id, string path) =>
		new(id, path, new JsonObject { ["id"] = id, ["path"] = path });

	private static LockSnapshot Snapshot(params (string Id, string Path)[] locks) =>
		new([.. locks.Select(entry => Entry(entry.Id, entry.Path))], TakenAt);

	[TestMethod]
	public void Filter_NoFilters_ReturnsEverythingWithoutCopying()
	{
		LockSnapshot snapshot = Snapshot(("1", "a.uasset"), ("2", "b.uasset"));

		Assert.AreSame(snapshot.Locks, snapshot.Filter(path: null, id: null));
	}

	[TestMethod]
	public void Filter_ByPath_MatchesExactly()
	{
		LockSnapshot snapshot = Snapshot(("1", "Content/a.uasset"), ("2", "Content/b.uasset"));

		IReadOnlyList<LockEntry> matches = snapshot.Filter("Content/a.uasset", id: null);

		Assert.ContainsSingle(matches);
		Assert.AreEqual("1", matches[0].Id);
	}

	[TestMethod]
	public void Filter_ByPath_IsNotAPrefixMatch()
	{
		// The specification's path filter is exact. Treating it as a prefix would report a lock on a
		// file nobody holds, which is worse than reporting none.
		LockSnapshot snapshot = Snapshot(("1", "Content/a.uasset"));

		Assert.IsEmpty(snapshot.Filter("Content", id: null));
	}

	[TestMethod]
	public void Filter_ByPath_IsCaseSensitive()
	{
		// Git paths are case sensitive, so two spellings are two files.
		LockSnapshot snapshot = Snapshot(("1", "Content/a.uasset"));

		Assert.IsEmpty(snapshot.Filter("content/A.uasset", id: null));
	}

	[TestMethod]
	public void Filter_ById_MatchesExactly()
	{
		LockSnapshot snapshot = Snapshot(("1", "a.uasset"), ("2", "b.uasset"));

		IReadOnlyList<LockEntry> matches = snapshot.Filter(path: null, id: "2");

		Assert.ContainsSingle(matches);
		Assert.AreEqual("b.uasset", matches[0].Path);
	}

	[TestMethod]
	public void Filter_BothFilters_MustBothMatch()
	{
		LockSnapshot snapshot = Snapshot(("1", "a.uasset"), ("2", "b.uasset"));

		Assert.IsEmpty(snapshot.Filter("a.uasset", "2"));
		Assert.ContainsSingle(snapshot.Filter("a.uasset", "1"));
	}

	[TestMethod]
	public void Paginate_WithinOnePage_ReportsNoNextOffset()
	{
		LockSnapshot snapshot = Snapshot(("1", "a"), ("2", "b"));

		(IReadOnlyList<LockEntry> page, int? next) = LockSnapshot.Paginate(snapshot.Locks, 0, 10);

		Assert.HasCount(2, page);
		Assert.IsNull(next);
	}

	[TestMethod]
	public void Paginate_AcrossPages_WalksEveryLockExactlyOnce()
	{
		LockSnapshot snapshot = Snapshot(("1", "a"), ("2", "b"), ("3", "c"), ("4", "d"), ("5", "e"));

		List<string> seen = [];
		int? offset = 0;

		while (offset is int position)
		{
			(IReadOnlyList<LockEntry> page, int? next) = LockSnapshot.Paginate(snapshot.Locks, position, 2);
			seen.AddRange(page.Select(entry => entry.Id));
			offset = next;
		}

		CollectionAssert.AreEqual(EveryIdInOrder, seen);
	}

	[TestMethod]
	public void Paginate_OffsetPastTheEnd_IsAnEmptyLastPage()
	{
		LockSnapshot snapshot = Snapshot(("1", "a"));

		(IReadOnlyList<LockEntry> page, int? next) = LockSnapshot.Paginate(snapshot.Locks, 99, 10);

		Assert.IsEmpty(page);
		Assert.IsNull(next);
	}

	[TestMethod]
	public void Paginate_ZeroLimit_DoesNotProduceACursorThatNeverAdvances()
	{
		// A next offset equal to the current one is a walk that never terminates.
		LockSnapshot snapshot = Snapshot(("1", "a"), ("2", "b"));

		(IReadOnlyList<LockEntry> page, int? next) = LockSnapshot.Paginate(snapshot.Locks, 0, 0);

		Assert.IsEmpty(page);
		Assert.IsNull(next);
	}

	[TestMethod]
	public void IsStale_AtExactlyTheMaximumAge_IsStale()
	{
		LockSnapshot snapshot = Snapshot(("1", "a"));

		Assert.IsTrue(snapshot.IsStale(TakenAt.AddSeconds(15), TimeSpan.FromSeconds(15)));
		Assert.IsFalse(snapshot.IsStale(TakenAt.AddSeconds(14), TimeSpan.FromSeconds(15)));
	}
}
