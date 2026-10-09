// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Locks;

using System.Text.Json.Nodes;
using ktsu.GitLfsCache.Locks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class LockFanOutRequestTests
{
	private static JsonNode Parse(string json) => JsonNode.Parse(json)!;

	[TestMethod]
	public void TryParse_RefObject_ReadsItsName()
	{
		JsonNode body = Parse("""{"operation":"lock","paths":["a.uasset"],"ref":{"name":"refs/heads/main"}}""");

		Assert.IsTrue(LockFanOutRequest.TryParse(body, out LockFanOutRequest? request));
		Assert.AreEqual("refs/heads/main", request.Ref);
	}

	[TestMethod]
	public void TryParse_NoRef_HasNoRef()
	{
		JsonNode body = Parse("""{"operation":"lock","paths":["a.uasset"]}""");

		Assert.IsTrue(LockFanOutRequest.TryParse(body, out LockFanOutRequest? request));
		Assert.IsNull(request.Ref);
	}

	[TestMethod]
	[DataRow("\"refs/heads/main\"")]
	[DataRow("1")]
	[DataRow("true")]
	[DataRow("[\"refs/heads/main\"]")]
	public void TryParse_RefThatIsNotAnObject_IsRefused(string refJson)
	{
		// Taking the lock with no ref would not be the lock the client asked for.
		JsonNode body = Parse($$"""{"operation":"lock","paths":["a.uasset"],"ref":{{refJson}}}""");

		Assert.IsFalse(LockFanOutRequest.TryParse(body, out LockFanOutRequest? request));
		Assert.IsNull(request);
	}
}
