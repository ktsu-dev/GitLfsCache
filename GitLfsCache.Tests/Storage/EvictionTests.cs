// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Storage;

using System.Security.Cryptography;
using System.Text;
using ktsu.GitLfsCache.Configuration;
using ktsu.GitLfsCache.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Testably.Abstractions.Testing;

[TestClass]
public class EvictionTests
{
	private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

	private static readonly string Root = Path.Combine(
		Path.GetPathRoot(Path.GetTempPath()) ?? Path.DirectorySeparatorChar.ToString(),
		"gitlfscache-eviction-tests");

	private static (ObjectStore Store, LeastRecentlyUsedEvictionPolicy Policy, FakeTimeProvider Time) Create(
		string maxSize,
		double lowWaterMark = 0.9)
	{
		(ObjectStore store, LeastRecentlyUsedEvictionPolicy policy, FakeTimeProvider time, _) = CreateWithFileSystem(maxSize, lowWaterMark);
		return (store, policy, time);
	}

	/// <summary>Builds the store and hands back the file system, for a test that has to disturb it.</summary>
	private static (ObjectStore Store, LeastRecentlyUsedEvictionPolicy Policy, FakeTimeProvider Time, MockFileSystem FileSystem) CreateWithFileSystem(
		string maxSize,
		double lowWaterMark = 0.9)
	{
		MockFileSystem fileSystem = new();
		fileSystem.Directory.CreateDirectory(Root);

		GitLfsCacheOptions options = new()
		{
			Store = new StoreOptions { Root = Root, MaxSize = maxSize, LowWaterMark = lowWaterMark },
		};

		FakeTimeProvider time = new(Now);
		IOptions<GitLfsCacheOptions> wrapped = Options.Create(options);
		ObjectStore store = new(fileSystem, wrapped, time, NullLogger<ObjectStore>.Instance);

		return (store, new LeastRecentlyUsedEvictionPolicy(store, wrapped), time, fileSystem);
	}

	/// <summary>Stores an object of exactly the requested size and returns its object id.</summary>
	private static async Task<string> StoreAsync(ObjectStore store, int sizeBytes, char fill)
	{
		byte[] content = Encoding.ASCII.GetBytes(new string(fill, sizeBytes));
		string oid = Convert.ToHexStringLower(SHA256.HashData(content));

		StagingHandle handle = store.OpenStaging("github");
		await handle.Stream.WriteAsync(content, CancellationToken.None);
		Assert.IsTrue(await store.PublishAsync(handle, "github", oid, CancellationToken.None));

		return oid;
	}

	[TestMethod]
	public async Task Evict_UnderBudget_DoesNothing()
	{
		(ObjectStore store, LeastRecentlyUsedEvictionPolicy policy, _) = Create("1KB");
		await StoreAsync(store, 100, 'a');

		EvictionResult result = policy.Evict();

		Assert.AreEqual(0, result.EvictedCount);
		Assert.AreEqual(0L, result.EvictedBytes);
		Assert.HasCount(1, store.Enumerate().ToList());
	}

	[TestMethod]
	public async Task Evict_OverBudget_DeletesColdestFirst()
	{
		(ObjectStore store, LeastRecentlyUsedEvictionPolicy policy, FakeTimeProvider time) = Create("300B");

		string coldest = await StoreAsync(store, 100, 'a');
		time.Advance(TimeSpan.FromHours(1));
		string middle = await StoreAsync(store, 100, 'b');
		time.Advance(TimeSpan.FromHours(1));
		string warmest = await StoreAsync(store, 100, 'c');
		time.Advance(TimeSpan.FromHours(1));
		await StoreAsync(store, 100, 'd');

		// 400 bytes stored against a 300 byte budget, so the sweep targets 270 bytes.
		EvictionResult result = policy.Evict();

		Assert.AreEqual(270L, result.TargetBytes);
		Assert.AreEqual(2, result.EvictedCount);
		Assert.AreEqual(200L, result.EvictedBytes);
		Assert.IsFalse(store.Exists("github", coldest));
		Assert.IsFalse(store.Exists("github", middle));
		Assert.IsTrue(store.Exists("github", warmest));
	}

	[TestMethod]
	public async Task Evict_StopsAtTheLowWaterMarkRatherThanTheBudget()
	{
		(ObjectStore store, LeastRecentlyUsedEvictionPolicy policy, FakeTimeProvider time) = Create("500B");

		for (int index = 0; index < 6; index++)
		{
			await StoreAsync(store, 100, (char)('a' + index));
			time.Advance(TimeSpan.FromMinutes(10));
		}

		EvictionResult result = policy.Evict();

		// 600 stored, 500 budget, 450 target: two objects go, not one.
		Assert.AreEqual(450L, result.TargetBytes);
		Assert.AreEqual(2, result.EvictedCount);
		Assert.AreEqual(400L, store.TotalBytes);
	}

	[TestMethod]
	public async Task Evict_TouchedObjectSurvivesAnOlderUntouchedOne()
	{
		(ObjectStore store, LeastRecentlyUsedEvictionPolicy policy, FakeTimeProvider time) = Create("300B");

		string old = await StoreAsync(store, 100, 'a');
		time.Advance(TimeSpan.FromHours(1));
		string young = await StoreAsync(store, 100, 'b');
		time.Advance(TimeSpan.FromHours(1));
		await StoreAsync(store, 100, 'c');
		time.Advance(TimeSpan.FromHours(1));
		await StoreAsync(store, 100, 'd');

		// Serving the oldest object makes it the warmest, which is the whole point of the access stamp.
		store.Touch("github", old);

		policy.Evict();

		Assert.IsTrue(store.Exists("github", old));
		Assert.IsFalse(store.Exists("github", young));
	}

	[TestMethod]
	public async Task Evict_ObjectThatCannotBeDeleted_IsSkippedAndReported()
	{
		(ObjectStore store, _, FakeTimeProvider time, _) = CreateWithFileSystem("100B");

		string undeletable = await StoreAsync(store, 100, 'a');
		time.Advance(TimeSpan.FromHours(1));
		await StoreAsync(store, 100, 'b');

		// Deletion is made to fail directly rather than by holding the file open. OpenRead asks for
		// FileShare.Delete precisely so a sweep can remove an object while it is being served, so an
		// open handle is the one thing that is not meant to stop this. What the sweep does have to
		// survive is a delete that fails for a reason it cannot control, and this reproduces that
		// the same way on every host.
		GitLfsCacheOptions options = new()
		{
			Store = new StoreOptions { Root = Root, MaxSize = "100B", LowWaterMark = 0.9 },
		};

		LeastRecentlyUsedEvictionPolicy policy = new(new UndeletableObjectStore(store, undeletable), Options.Create(options));

		EvictionResult result = policy.Evict();

		Assert.AreEqual(1, result.SkippedCount);
		Assert.IsTrue(store.Exists("github", undeletable));
	}

	[TestMethod]
	public void Evict_EmptyStore_DoesNothing()
	{
		(_, LeastRecentlyUsedEvictionPolicy policy, _) = Create("1KB");

		EvictionResult result = policy.Evict();

		Assert.AreEqual(0, result.EvictedCount);
		Assert.AreEqual(0, result.SkippedCount);
	}
}
