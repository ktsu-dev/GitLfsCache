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
public class ObjectStoreTests
{
	private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

	/// <summary>
	/// An absolute root for whichever platform the suite runs on. AbsoluteDirectoryPath requires a
	/// fully qualified path, so a hard-coded POSIX root would be rejected on Windows.
	/// </summary>
	private static readonly string Root = Path.Combine(
		Path.GetPathRoot(Path.GetTempPath()) ?? Path.DirectorySeparatorChar.ToString(),
		"gitlfscache-tests");

	private static (ObjectStore Store, MockFileSystem FileSystem, FakeTimeProvider Time) Create()
	{
		MockFileSystem fileSystem = new();
		fileSystem.Directory.CreateDirectory(Root);

		GitLfsCacheOptions options = new()
		{
			Store = new StoreOptions { Root = Root, MaxSize = "1GB", LowWaterMark = 0.9 },
		};

		FakeTimeProvider time = new(Now);

		ObjectStore store = new(fileSystem, Options.Create(options), time, NullLogger<ObjectStore>.Instance);

		return (store, fileSystem, time);
	}

	private static (byte[] Content, string Oid) Content(string text)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(text);
		return (bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)));
	}

	private static async Task<string> StoreAsync(ObjectStore store, string upstream, string text)
	{
		(byte[] content, string oid) = Content(text);

		StagingHandle handle = store.OpenStaging(upstream);
		await handle.Stream.WriteAsync(content, CancellationToken.None);
		bool published = await store.PublishAsync(handle, upstream, oid, CancellationToken.None);

		Assert.IsTrue(published, "Publishing a correctly hashed object should succeed.");
		return oid;
	}

	[TestMethod]
	public async Task PublishAsync_CorrectHash_MakesTheObjectReadable()
	{
		(ObjectStore store, _, _) = Create();
		(byte[] content, _) = Content("hello lfs");

		string oid = await StoreAsync(store, "github", "hello lfs");

		Assert.IsTrue(store.TryOpenRead("github", oid, out Stream? stream, out long length));
		Assert.IsNotNull(stream);
		Assert.AreEqual(content.Length, length);

		await using (stream)
		{
			using MemoryStream read = new();
			await stream.CopyToAsync(read, CancellationToken.None);
			CollectionAssert.AreEqual(content, read.ToArray());
		}
	}

	[TestMethod]
	public async Task PublishAsync_UsesTwoLevelFanOutUnderTheUpstreamKey()
	{
		(ObjectStore store, MockFileSystem fileSystem, _) = Create();

		string oid = await StoreAsync(store, "github", "fan out");

		string expected = fileSystem.Path.Combine(Root, "github", "objects", oid[..2], oid[2..4], oid);

		Assert.IsTrue(fileSystem.File.Exists(expected), $"Expected the object at {expected}.");
	}

	[TestMethod]
	public async Task PublishAsync_HashMismatch_DiscardsStagingAndReturnsFalse()
	{
		(ObjectStore store, MockFileSystem fileSystem, _) = Create();
		const string wrongOid = "0000000000000000000000000000000000000000000000000000000000000000";

		StagingHandle handle = store.OpenStaging("github");
		string stagingPath = handle.Path;
		await handle.Stream.WriteAsync(Encoding.UTF8.GetBytes("not what the oid claims"), CancellationToken.None);

		bool published = await store.PublishAsync(handle, "github", wrongOid, CancellationToken.None);

		Assert.IsFalse(published);
		Assert.IsFalse(store.Exists("github", wrongOid));
		Assert.IsFalse(fileSystem.File.Exists(stagingPath), "Staging must not survive a mismatch.");
	}

	[TestMethod]
	public async Task PublishAsync_ObjectAlreadyPresent_SucceedsAndRemovesStaging()
	{
		(ObjectStore store, MockFileSystem fileSystem, _) = Create();
		string oid = await StoreAsync(store, "github", "duplicate");
		(byte[] content, _) = Content("duplicate");

		StagingHandle second = store.OpenStaging("github");
		string stagingPath = second.Path;
		await second.Stream.WriteAsync(content, CancellationToken.None);

		bool published = await store.PublishAsync(second, "github", oid, CancellationToken.None);

		Assert.IsTrue(published);
		Assert.IsFalse(fileSystem.File.Exists(stagingPath));
	}

	[TestMethod]
	public async Task StagingHandle_DisposedWithoutPublishing_DeletesTheFile()
	{
		(ObjectStore store, MockFileSystem fileSystem, _) = Create();

		StagingHandle handle = store.OpenStaging("github");
		string path = handle.Path;
		await handle.Stream.WriteAsync(Encoding.UTF8.GetBytes("abandoned"), CancellationToken.None);
		await handle.DisposeAsync();

		Assert.IsFalse(fileSystem.File.Exists(path));
	}

	[TestMethod]
	public void TryOpenRead_MissingObject_ReturnsFalse()
	{
		(ObjectStore store, _, _) = Create();

		bool opened = store.TryOpenRead("github", new string('a', 64), out Stream? stream, out long length);

		using (stream)
		{
			Assert.IsFalse(opened);
			Assert.IsNull(stream);
			Assert.AreEqual(0L, length);
		}
	}

	[TestMethod]
	public async Task Upstreams_AreNamespacedSeparately()
	{
		(ObjectStore store, _, _) = Create();

		string oid = await StoreAsync(store, "github", "shared bytes");

		Assert.IsTrue(store.Exists("github", oid));
		Assert.IsFalse(store.Exists("ado", oid), "Object trees must not be shared between upstreams.");
	}

	[TestMethod]
	[DataRow("../../etc/passwd")]
	[DataRow("abc")]
	[DataRow("")]
	[DataRow("ZZZZ111111111111111111111111111111111111111111111111111111111111")]
	[DataRow("9A1F2B3C4D5E6F708192A3B4C5D6E7F8091A2B3C4D5E6F708192A3B4C5D6E7F8")]
	public void TryOpenRead_MalformedOid_ReturnsFalse(string oid)
	{
		(ObjectStore store, _, _) = Create();

		bool opened = store.TryOpenRead("github", oid, out Stream? stream, out long _);

		using (stream)
		{
			Assert.IsFalse(opened);
		}
	}

	[TestMethod]
	[DataRow("../escape")]
	[DataRow("with/slash")]
	[DataRow("")]
	public void OpenStaging_MalformedUpstream_Throws(string upstream)
	{
		(ObjectStore store, _, _) = Create();

		Assert.ThrowsExactly<ArgumentException>(() => store.OpenStaging(upstream));
	}

	[TestMethod]
	public async Task Touch_UpdatesLastAccessTime()
	{
		(ObjectStore store, _, FakeTimeProvider time) = Create();
		await StoreAsync(store, "github", "touch me");

		time.Advance(TimeSpan.FromHours(3));
		store.Touch("github", store.Enumerate().Single().Oid);

		StoredObject stored = store.Enumerate().Single();
		Assert.AreEqual(Now.AddHours(3).UtcDateTime, stored.LastAccessUtc.UtcDateTime);
	}

	[TestMethod]
	public void Touch_MissingObject_DoesNotThrow()
	{
		(ObjectStore store, _, _) = Create();

		store.Touch("github", new string('b', 64));
	}

	[TestMethod]
	public async Task Enumerate_ReturnsEveryStoredObjectAcrossUpstreams()
	{
		(ObjectStore store, _, _) = Create();
		await StoreAsync(store, "github", "one");
		await StoreAsync(store, "github", "two");
		await StoreAsync(store, "ado", "three");

		Assert.HasCount(3, store.Enumerate().ToList());
	}

	[TestMethod]
	public async Task Enumerate_ReportsSizeUpstreamAndOid()
	{
		(ObjectStore store, _, _) = Create();
		(byte[] content, _) = Content("measured");
		string oid = await StoreAsync(store, "github", "measured");

		StoredObject stored = store.Enumerate().Single();

		Assert.AreEqual(oid, stored.Oid);
		Assert.AreEqual("github", stored.Upstream);
		Assert.AreEqual(content.Length, stored.Size);
	}

	[TestMethod]
	public async Task Enumerate_IgnoresFilesThatAreNotNamedLikeAnObjectId()
	{
		(ObjectStore store, MockFileSystem fileSystem, _) = Create();
		await StoreAsync(store, "github", "real object");
		string strayDirectory = fileSystem.Path.Combine(Root, "github", "objects", "zz", "zz");
		fileSystem.Directory.CreateDirectory(strayDirectory);
		await fileSystem.File.WriteAllTextAsync(
			fileSystem.Path.Combine(strayDirectory, "notes.txt"),
			"dropped by hand",
			CancellationToken.None);

		Assert.HasCount(1, store.Enumerate().ToList());
	}

	[TestMethod]
	public async Task TotalBytes_TracksPublishAndDelete()
	{
		(ObjectStore store, _, _) = Create();
		(byte[] content, _) = Content("accounted for");

		await StoreAsync(store, "github", "accounted for");
		Assert.AreEqual(content.Length, store.TotalBytes);

		Assert.IsTrue(store.TryDelete(store.Enumerate().Single()));
		Assert.AreEqual(0L, store.TotalBytes);
	}

	[TestMethod]
	public async Task RecomputeTotalBytes_RebuildsTheCounterFromDisk()
	{
		(ObjectStore store, _, _) = Create();
		(byte[] content, _) = Content("rebuilt");
		await StoreAsync(store, "github", "rebuilt");

		store.RecomputeTotalBytes();

		Assert.AreEqual(content.Length, store.TotalBytes);
	}

	[TestMethod]
	public async Task EnumerateStaging_OrphanedFile_IsReportedAndCanBeDeleted()
	{
		(ObjectStore store, _, _) = Create();
		StagingHandle handle = store.OpenStaging("github");
		await handle.Stream.WriteAsync(Encoding.UTF8.GetBytes("orphan"), CancellationToken.None);

		// Close without publishing and without disposing, which is what a crashed write leaves
		// behind: a staging file on disk that no live request holds.
		await handle.CloseAsync(CancellationToken.None);

		StagedFile staged = store.EnumerateStaging().Single();

		// Creation time comes from the filesystem rather than the injected clock, because orphan
		// cleanup has to work across a restart where the process clock tells it nothing. So this
		// asserts the stamp is recent, not that it matches the fake clock.
		Assert.IsTrue(
			DateTimeOffset.UtcNow - staged.CreatedUtc < TimeSpan.FromMinutes(1),
			$"Expected a recent creation stamp but got {staged.CreatedUtc:O}.");
		Assert.IsTrue(store.TryDeleteStaging(staged));
		Assert.HasCount(0, store.EnumerateStaging().ToList());
	}

	[TestMethod]
	public async Task TryDeleteStaging_FileStillBeingWritten_DoesNotDeleteIt()
	{
		(ObjectStore store, _, _) = Create();
		StagingHandle handle = store.OpenStaging("github");

		await using (handle)
		{
			await handle.Stream.WriteAsync(Encoding.UTF8.GetBytes("in flight"), CancellationToken.None);

			StagedFile staged = store.EnumerateStaging().Single();

			// Cleanup must never pull a staging file out from under a transfer that is still writing
			// to it. Reporting false and retrying later is the correct outcome.
			Assert.IsFalse(store.TryDeleteStaging(staged));
		}
	}

	[TestMethod]
	public void EnumerateStaging_NoStoreYet_ReturnsNothing()
	{
		MockFileSystem fileSystem = new();
		GitLfsCacheOptions options = new() { Store = new StoreOptions { Root = Root, MaxSize = "1GB" } };
		ObjectStore store = new(
			fileSystem,
			Options.Create(options),
			new FakeTimeProvider(Now),
			NullLogger<ObjectStore>.Instance);

		Assert.HasCount(0, store.Enumerate().ToList());
		Assert.HasCount(0, store.EnumerateStaging().ToList());
	}
}
