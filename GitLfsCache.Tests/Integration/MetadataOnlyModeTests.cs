// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Integration;

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ktsu.GitLfsCache.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// A deployment with <c>Store:Enabled</c> false, for placing next to a forge to serve the lock plane
/// while object bytes go somewhere closer to the client.
/// </summary>
[TestClass]
public class MetadataOnlyModeTests
{
	private const string LfsPath = "/github/owner/repo.git/info/lfs";
	private const string Credential = "Basic dXNlcjp0b2tlbg==";

	private static Dictionary<string, string?> MetadataOnly() => new(StringComparer.Ordinal)
	{
		["GitLfsCache:Store:Enabled"] = "false",
	};

	private static (byte[] Content, string Oid) Object(string text)
	{
		byte[] content = Encoding.UTF8.GetBytes(text);
		return (content, Convert.ToHexStringLower(SHA256.HashData(content)));
	}

	private static async Task<JsonNode> PostBatchAsync(ProxyFixture fixture, string oid, long size)
	{
		using HttpClient client = fixture.Client;

		using HttpRequestMessage request = new(HttpMethod.Post, $"{LfsPath}/objects/batch")
		{
			Content = new StringContent(
				$$"""{"operation":"download","transfers":["basic"],"objects":[{"oid":"{{oid}}","size":{{size}}}],"hash_algo":"sha256"}""",
				Encoding.UTF8,
				"application/vnd.git-lfs+json"),
		};

		request.Headers.TryAddWithoutValidation("Authorization", Credential);

		using HttpResponseMessage response = await client.SendAsync(request);

		Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
		return (await response.Content.ReadFromJsonAsync<JsonNode>())!;
	}

	[TestMethod]
	public async Task Batch_IsRelayedUnrewritten_SoObjectBytesNeverCrossThisProcess()
	{
		// The point of the mode. A client is handed upstream's own hrefs and goes straight there,
		// rather than being pointed back at a deployment that has no disk to serve them from.
		await using ProxyFixture fixture = await ProxyFixture.StartAsync(MetadataOnly());
		(byte[] content, string oid) = Object("straight to upstream");
		fixture.Upstream.AddObject(oid, content);

		JsonNode batch = await PostBatchAsync(fixture, oid, content.Length);
		string href = batch["objects"]![0]!["actions"]!["download"]!["href"]!.GetValue<string>();

		Assert.AreEqual($"https://upstream.example/storage/{oid}", href);
		Assert.DoesNotContain("cache.example", batch.ToJsonString());
	}

	[TestMethod]
	public async Task Batch_KeepsTheUpstreamActionHeaders()
	{
		// In caching mode those credentials move into the rewritten token and the header map is
		// dropped. With nothing rewritten they have to survive, or the client cannot authenticate the
		// transfer it was just told to make.
		await using ProxyFixture fixture = await ProxyFixture.StartAsync(MetadataOnly());
		(byte[] content, string oid) = Object("headers survive");
		fixture.Upstream.AddObject(oid, content);

		JsonNode batch = await PostBatchAsync(fixture, oid, content.Length);

		Assert.AreEqual(
			"Bearer upstream-download-secret",
			batch["objects"]![0]!["actions"]!["download"]!["header"]!["Authorization"]!.GetValue<string>());
	}

	[TestMethod]
	public async Task NoStoreIsConstructed()
	{
		// ObjectStore resolves its root in its constructor, so a deployment with no root would fail on
		// the first request if the real store were still registered.
		await using ProxyFixture fixture = await ProxyFixture.StartAsync(MetadataOnly());

		Assert.IsInstanceOfType<NullObjectStore>(fixture.Store);
	}

	[TestMethod]
	public async Task ReadinessDoesNotWaitForAStoreCheckThatNeverRuns()
	{
		await using ProxyFixture fixture = await ProxyFixture.StartAsync(MetadataOnly());
		using HttpClient client = fixture.Client;

		using HttpResponseMessage response = await client.GetAsync("/readyz");

		Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
	}

	[TestMethod]
	public async Task LockListingIsStillCached()
	{
		// The whole reason to run in this mode.
		await using ProxyFixture fixture = await ProxyFixture.StartAsync(MetadataOnly());
		fixture.Upstream.Locks.AddRange(["a", "b"]);

		using HttpClient client = fixture.Client;

		for (int attempt = 0; attempt < 3; attempt++)
		{
			using HttpRequestMessage request = new(HttpMethod.Get, $"{LfsPath}/locks");
			request.Headers.TryAddWithoutValidation("Authorization", Credential);

			using HttpResponseMessage response = await client.SendAsync(request);
			Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

			JsonNode listing = (await response.Content.ReadFromJsonAsync<JsonNode>())!;
			Assert.HasCount(2, listing["locks"]!.AsArray());
		}

		Assert.AreEqual(1, fixture.Upstream.LockPageRequests);
	}

	[TestMethod]
	public async Task ObjectTransfer_IsRelayedRatherThanServed()
	{
		// Nothing issues a token in this mode, so a transfer path can only be reached by a token from a
		// sibling deployment or by a client inventing one. Either way it relays rather than touching a
		// store that does not exist.
		await using ProxyFixture fixture = await ProxyFixture.StartAsync(MetadataOnly());
		(_, string oid) = Object("no token here");

		using HttpClient client = fixture.Client;
		using HttpResponseMessage response = await client.GetAsync($"{LfsPath}/objects/{oid}");

		// Relayed, so upstream answered: in caching mode this is a 403 from the token check instead.
		Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
		Assert.Contains("relayed", await response.Content.ReadAsStringAsync());
	}
}
