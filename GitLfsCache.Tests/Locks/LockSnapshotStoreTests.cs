// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Locks;

using System.Globalization;
using ktsu.GitLfsCache.Configuration;
using ktsu.GitLfsCache.Locks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class LockSnapshotStoreTests
{
	private const string Upstream = "github";
	private const string Repository = "owner/repo.git/info/lfs";

	private static readonly TimeSpan ListTtl = TimeSpan.FromSeconds(15);

	private static (LockSnapshotStore Store, FakeTimeProvider Time) Build(int maxSnapshots = 1000)
	{
		GitLfsCacheOptions options = new()
		{
			Locks = new LocksOptions { ListTtl = ListTtl, MaxSnapshots = maxSnapshots },
		};

		FakeTimeProvider time = new(new DateTimeOffset(2026, 8, 19, 9, 47, 0, TimeSpan.Zero));

		return (new LockSnapshotStore(Options.Create(options), time), time);
	}

	private static LockSnapshotKey Key(int branch) =>
		new(Upstream, Repository, string.Create(CultureInfo.InvariantCulture, $"refs/heads/b{branch}"));

	private static LockSnapshot Snapshot(FakeTimeProvider time) => new([], time.GetUtcNow());

	[TestMethod]
	public void Publish_AfterManyRefsHaveOutlivedTheListTtl_DropsEveryStaleSnapshot()
	{
		// The ref comes from the client's query string, so a client can name as many as it likes.
		(LockSnapshotStore store, FakeTimeProvider time) = Build();

		for (int branch = 0; branch < 200; branch++)
		{
			store.Publish(Key(branch), Snapshot(time));
		}

		Assert.AreEqual(200, store.Count);

		time.Advance(ListTtl);
		store.Publish(Key(200), Snapshot(time));

		Assert.AreEqual(1, store.Count);
		Assert.IsNull(store.Read(Key(0)));
		Assert.IsNull(store.Read(Key(199)));
		Assert.IsNotNull(store.Read(Key(200)));
	}

	[TestMethod]
	public void Publish_BeyondMaxSnapshots_EvictsTheOldestFirst()
	{
		(LockSnapshotStore store, FakeTimeProvider time) = Build(maxSnapshots: 3);

		for (int branch = 0; branch < 5; branch++)
		{
			store.Publish(Key(branch), Snapshot(time));
			time.Advance(TimeSpan.FromSeconds(1));
		}

		Assert.AreEqual(3, store.Count);
		Assert.IsNull(store.Read(Key(0)));
		Assert.IsNull(store.Read(Key(1)));
		Assert.IsNotNull(store.Read(Key(2)));
		Assert.IsNotNull(store.Read(Key(3)));
		Assert.IsNotNull(store.Read(Key(4)));
	}

	[TestMethod]
	public void Publish_ASnapshotThatIsAlreadyOld_KeepsIt()
	{
		// A long walk can finish with a snapshot that is already past its lifetime. Evicting it on the
		// way in would throw away the work that produced it.
		(LockSnapshotStore store, FakeTimeProvider time) = Build(maxSnapshots: 1);
		LockSnapshot old = Snapshot(time);
		time.Advance(ListTtl * 2);

		store.Publish(Key(0), old);

		Assert.AreSame(old, store.Read(Key(0)));
	}

	[TestMethod]
	public void Publish_RepublishingOneKey_HoldsOneSnapshot()
	{
		(LockSnapshotStore store, FakeTimeProvider time) = Build();

		store.Publish(Key(0), Snapshot(time));
		LockSnapshot latest = Snapshot(time);
		store.Publish(Key(0), latest);

		Assert.AreEqual(1, store.Count);
		Assert.AreSame(latest, store.Read(Key(0)));
	}

	[TestMethod]
	public void Invalidate_DropsEveryRefOfTheRepositoryOnly()
	{
		(LockSnapshotStore store, FakeTimeProvider time) = Build();
		LockSnapshotKey other = new(Upstream, "owner/other.git/info/lfs", null);

		store.Publish(Key(0), Snapshot(time));
		store.Publish(Key(1), Snapshot(time));
		store.Publish(other, Snapshot(time));

		store.Invalidate(Upstream, Repository);

		Assert.IsNull(store.Read(Key(0)));
		Assert.IsNull(store.Read(Key(1)));
		Assert.IsNotNull(store.Read(other));
	}
}
