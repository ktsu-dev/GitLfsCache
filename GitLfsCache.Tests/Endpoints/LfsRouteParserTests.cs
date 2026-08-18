// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Endpoints;

using ktsu.GitLfsCache.Endpoints;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class LfsRouteParserTests
{
	private const string Oid = "9a1f2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e7f8";

	private static LfsRoute Parse(string path)
	{
		Assert.IsTrue(LfsRouteParser.TryParse(path, out LfsRoute? route), $"Failed to parse '{path}'.");
		return route;
	}

	[TestMethod]
	public void TryParse_BatchPath_IsRecognized()
	{
		LfsRoute route = Parse("/github/owner/repo.git/info/lfs/objects/batch");

		Assert.AreEqual(LfsRouteKind.Batch, route.Kind);
		Assert.AreEqual("github", route.Upstream);
		Assert.AreEqual("owner/repo.git/info/lfs", route.RepositoryPath);
		Assert.IsNull(route.Oid);
	}

	[TestMethod]
	public void TryParse_DeeperRepositoryPath_IsRecognized()
	{
		LfsRoute route = Parse("/ado/org/project/_git/repo/info/lfs/objects/batch");

		Assert.AreEqual(LfsRouteKind.Batch, route.Kind);
		Assert.AreEqual("ado", route.Upstream);
		Assert.AreEqual("org/project/_git/repo/info/lfs", route.RepositoryPath);
	}

	[TestMethod]
	public void TryParse_ObjectPath_IsRecognized()
	{
		LfsRoute route = Parse($"/github/owner/repo.git/info/lfs/objects/{Oid}");

		Assert.AreEqual(LfsRouteKind.Transfer, route.Kind);
		Assert.AreEqual("owner/repo.git/info/lfs", route.RepositoryPath);
		Assert.AreEqual(Oid, route.Oid);
	}

	[TestMethod]
	public void TryParse_VerifyPath_IsRecognized()
	{
		LfsRoute route = Parse($"/github/owner/repo.git/info/lfs/objects/{Oid}/verify");

		Assert.AreEqual(LfsRouteKind.Verify, route.Kind);
		Assert.AreEqual("owner/repo.git/info/lfs", route.RepositoryPath);
		Assert.AreEqual(Oid, route.Oid);
	}

	[TestMethod]
	public void TryParse_LocksPath_FallsThroughToRelay()
	{
		LfsRoute route = Parse("/github/owner/repo.git/info/lfs/locks");

		Assert.AreEqual(LfsRouteKind.Relay, route.Kind);
		Assert.AreEqual("owner/repo.git/info/lfs/locks", route.RelayPath);
	}

	[TestMethod]
	public void TryParse_UnknownLfsFeature_FallsThroughToRelay()
	{
		LfsRoute route = Parse("/github/owner/repo.git/info/lfs/some/future/endpoint");

		Assert.AreEqual(LfsRouteKind.Relay, route.Kind);
		Assert.AreEqual("owner/repo.git/info/lfs/some/future/endpoint", route.RelayPath);
	}

	[TestMethod]
	[DataRow("/github/owner/repo/objects/abc")]
	[DataRow("/github/owner/repo/objects/9A1F2B3C4D5E6F708192A3B4C5D6E7F8091A2B3C4D5E6F708192A3B4C5D6E7F8")]
	[DataRow("/github/owner/repo/objects/zz1f2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e7f8")]
	public void TryParse_SegmentThatIsNotAnObjectId_FallsThroughToRelay(string path)
	{
		// Uppercase is deliberately rejected: two spellings of one object would produce two store
		// entries for identical bytes.
		Assert.AreEqual(LfsRouteKind.Relay, Parse(path).Kind);
	}

	[TestMethod]
	public void TryParse_UpstreamOnly_IsARelay()
	{
		LfsRoute route = Parse("/github");

		Assert.AreEqual(LfsRouteKind.Relay, route.Kind);
		Assert.AreEqual("github", route.Upstream);
		Assert.AreEqual(string.Empty, route.RelayPath);
	}

	[TestMethod]
	public void TryParse_NoLeadingSlash_ParsesTheSameWay()
	{
		Assert.AreEqual(LfsRouteKind.Batch, Parse("github/owner/repo.git/info/lfs/objects/batch").Kind);
	}

	[TestMethod]
	public void TryParse_RepeatedSlashes_AreIgnored()
	{
		LfsRoute route = Parse("//github//owner/repo.git/info/lfs//objects/batch");

		Assert.AreEqual(LfsRouteKind.Batch, route.Kind);
		Assert.AreEqual("github", route.Upstream);
	}

	[TestMethod]
	[DataRow(null)]
	[DataRow("")]
	[DataRow("/")]
	[DataRow("   ")]
	public void TryParse_NoUpstreamSegment_ReturnsFalse(string? path)
	{
		Assert.IsFalse(LfsRouteParser.TryParse(path, out LfsRoute? route));
		Assert.IsNull(route);
	}

	[TestMethod]
	public void TryParse_BatchAtTheRootOfAnUpstream_HasAnEmptyRepositoryPath()
	{
		LfsRoute route = Parse("/github/objects/batch");

		Assert.AreEqual(LfsRouteKind.Batch, route.Kind);
		Assert.AreEqual(string.Empty, route.RepositoryPath);
	}

	[TestMethod]
	public void TryParse_ObjectNamedBatch_IsStillABatchRoute()
	{
		// A path ending in objects/batch is the Batch API, never an object named "batch", because
		// "batch" is not a valid object id.
		Assert.AreEqual(LfsRouteKind.Batch, Parse("/github/repo/objects/batch").Kind);
	}
}
