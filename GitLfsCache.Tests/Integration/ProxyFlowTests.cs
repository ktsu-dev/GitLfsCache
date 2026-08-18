// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Integration;

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class ProxyFlowTests
{
	private const string LfsPath = "/github/owner/repo.git/info/lfs";

	private static (byte[] Content, string Oid) Object(string text)
	{
		byte[] content = Encoding.UTF8.GetBytes(text);
		return (content, Convert.ToHexStringLower(SHA256.HashData(content)));
	}

	private static StringContent BatchRequest(string operation, string oid, long size) =>
		new(
			$$"""{"operation":"{{operation}}","transfers":["basic"],"objects":[{"oid":"{{oid}}","size":{{size}}}],"hash_algo":"sha256"}""",
			Encoding.UTF8,
			"application/vnd.git-lfs+json");

	private static async Task<JsonNode> PostBatchAsync(
		ProxyFixture fixture,
		string operation,
		string oid,
		long size,
		string? authorization = "Basic dXNlcjp0b2tlbg==")
	{
		using HttpClient client = fixture.Client;

		using StringContent body = BatchRequest(operation, oid, size);

		using HttpRequestMessage request = new(HttpMethod.Post, $"{LfsPath}/objects/batch")
		{
			Content = body,
		};

		if (authorization is not null)
		{
			request.Headers.TryAddWithoutValidation("Authorization", authorization);
		}

		using HttpResponseMessage response = await client.SendAsync(request);

		Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
		return (await response.Content.ReadFromJsonAsync<JsonNode>())!;
	}

	private static string HrefOf(JsonNode batch, string action) =>
		batch["objects"]![0]!["actions"]![action]!["href"]!.GetValue<string>();

	/// <summary>Turns a rewritten absolute href into a relative URL the test client can request.</summary>
	private static string Relative(string href) => new Uri(href).PathAndQuery;

	[TestMethod]
	public async Task Batch_IsRelayedUpstreamWithTheClientCredential()
	{
		await using ProxyFixture fixture = await ProxyFixture.StartAsync();
		(byte[] content, string oid) = Object("relayed batch");
		fixture.Upstream.AddObject(oid, content);

		await PostBatchAsync(fixture, "download", oid, content.Length);

		StubUpstream.RecordedRequest batch = fixture.Upstream.Requests.Single();
		Assert.AreEqual("POST", batch.Method);
		Assert.EndsWith("/owner/repo.git/info/lfs/objects/batch", batch.Path);
		Assert.AreEqual("Basic dXNlcjp0b2tlbg==", batch.Authorization);
	}

	[TestMethod]
	public async Task Batch_RewritesTheHrefToPointAtTheProxyAndHidesTheUpstreamCredential()
	{
		await using ProxyFixture fixture = await ProxyFixture.StartAsync();
		(byte[] content, string oid) = Object("rewritten");
		fixture.Upstream.AddObject(oid, content);

		JsonNode batch = await PostBatchAsync(fixture, "download", oid, content.Length);

		Assert.StartsWith($"https://cache.example/github/owner/repo.git/info/lfs/objects/{oid}?t=", HrefOf(batch, "download"));
		Assert.DoesNotContain("upstream-download-secret", batch.ToJsonString());
		Assert.IsFalse(batch["objects"]![0]!["actions"]!["download"]!.AsObject().ContainsKey("header"));
	}

	[TestMethod]
	public async Task Batch_PreservesUpstreamPropertiesTheProxyDoesNotModel()
	{
		await using ProxyFixture fixture = await ProxyFixture.StartAsync();
		(byte[] content, string oid) = Object("preserved");
		fixture.Upstream.AddObject(oid, content);

		JsonNode batch = await PostBatchAsync(fixture, "download", oid, content.Length);

		Assert.AreEqual("sha256", batch["hash_algo"]!.GetValue<string>());
		Assert.IsTrue(batch["objects"]![0]!["authenticated"]!.GetValue<bool>());
	}

	[TestMethod]
	public async Task Batch_UpstreamRefusal_IsRelayedVerbatim()
	{
		await using ProxyFixture fixture = await ProxyFixture.StartAsync();
		fixture.Upstream.BatchStatus = HttpStatusCode.Forbidden;
		(byte[] content, string oid) = Object("denied");
		using HttpClient client = fixture.Client;

		using StringContent body = BatchRequest("download", oid, content.Length);

		using HttpResponseMessage response = await client.PostAsync($"{LfsPath}/objects/batch", body);

		// Upstream is the authority on access, so its answer reaches the client unchanged.
		Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
		Assert.Contains("Repository not found", await response.Content.ReadAsStringAsync());
	}

	[TestMethod]
	public async Task Download_ColdThenWarm_FetchesUpstreamOnceAndServesFromTheStore()
	{
		await using ProxyFixture fixture = await ProxyFixture.StartAsync();
		(byte[] content, string oid) = Object("clone me twice");
		fixture.Upstream.AddObject(oid, content);

		// First clone: batch, miss, fetch and store on the way through.
		JsonNode firstBatch = await PostBatchAsync(fixture, "download", oid, content.Length);
		using HttpClient client = fixture.Client;
		byte[] firstBody = await client.GetByteArrayAsync(Relative(HrefOf(firstBatch, "download")));

		CollectionAssert.AreEqual(content, firstBody);
		Assert.AreEqual(1, fixture.Upstream.FetchCount(oid));
		Assert.IsTrue(fixture.Store.Exists("github", oid));

		// Second clone: batch is still relayed, but the bytes now come from the store.
		JsonNode secondBatch = await PostBatchAsync(fixture, "download", oid, content.Length);
		byte[] secondBody = await client.GetByteArrayAsync(Relative(HrefOf(secondBatch, "download")));

		CollectionAssert.AreEqual(content, secondBody);
		Assert.AreEqual(1, fixture.Upstream.FetchCount(oid));
	}

	[TestMethod]
	public async Task Download_NeverShortCircuitsTheBatchCall()
	{
		await using ProxyFixture fixture = await ProxyFixture.StartAsync();
		(byte[] content, string oid) = Object("authorization matters");
		fixture.Upstream.AddObject(oid, content);

		JsonNode batch = await PostBatchAsync(fixture, "download", oid, content.Length);
		using HttpClient client = fixture.Client;
		await client.GetByteArrayAsync(Relative(HrefOf(batch, "download")));

		int batchCallsBefore = fixture.Upstream.Requests.Count(request => request.Path.EndsWith("batch", StringComparison.Ordinal));

		await PostBatchAsync(fixture, "download", oid, content.Length);

		int batchCallsAfter = fixture.Upstream.Requests.Count(request => request.Path.EndsWith("batch", StringComparison.Ordinal));

		// A cached object must not let a client skip upstream's access check.
		Assert.AreEqual(batchCallsBefore + 1, batchCallsAfter);
	}

	[TestMethod]
	public async Task Download_CorruptUpstreamContent_IsNeitherServedAsAHitNorStored()
	{
		await using ProxyFixture fixture = await ProxyFixture.StartAsync();
		(byte[] content, string oid) = Object("honest bytes");
		fixture.Upstream.AddObject(oid, content);
		fixture.Upstream.CorruptedContent = Encoding.UTF8.GetBytes("tampered bytes");

		JsonNode batch = await PostBatchAsync(fixture, "download", oid, content.Length);
		using HttpClient client = fixture.Client;
		using HttpResponseMessage response = await client.GetAsync(Relative(HrefOf(batch, "download")));

		Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
		Assert.IsFalse(
			fixture.Store.Exists("github", oid),
			"Content that did not hash to the object id must never enter the store.");
	}

	[TestMethod]
	public async Task Download_WithoutAToken_IsRefused()
	{
		await using ProxyFixture fixture = await ProxyFixture.StartAsync();
		(_, string oid) = Object("no token");
		using HttpClient client = fixture.Client;

		using HttpResponseMessage response = await client.GetAsync($"{LfsPath}/objects/{oid}");

		Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[TestMethod]
	public async Task Download_WithAForgedToken_IsRefused()
	{
		await using ProxyFixture fixture = await ProxyFixture.StartAsync();
		(_, string oid) = Object("forged");
		using HttpClient client = fixture.Client;

		using HttpResponseMessage response = await client.GetAsync(
			$"{LfsPath}/objects/{oid}?t=bm90LWEtcmVhbC10b2tlbg");

		Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[TestMethod]
	public async Task Download_TokenForADifferentObject_IsRefused()
	{
		await using ProxyFixture fixture = await ProxyFixture.StartAsync();
		(byte[] content, string oid) = Object("bound to one object");
		fixture.Upstream.AddObject(oid, content);
		JsonNode batch = await PostBatchAsync(fixture, "download", oid, content.Length);

		string token = new Uri(HrefOf(batch, "download")).Query;
		string otherOid = new('c', 64);
		using HttpClient client = fixture.Client;

		using HttpResponseMessage response = await client.GetAsync(
			$"{LfsPath}/objects/{otherOid}{token}");

		Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[TestMethod]
	public async Task Download_UploadTokenOnTheDownloadPath_IsRefused()
	{
		await using ProxyFixture fixture = await ProxyFixture.StartAsync();
		(byte[] content, string oid) = Object("wrong action");
		JsonNode batch = await PostBatchAsync(fixture, "upload", oid, content.Length);

		string uploadHref = Relative(HrefOf(batch, "upload"));
		using HttpClient client = fixture.Client;

		// Download and upload share a path and differ only by method, so the token names its action.
		using HttpResponseMessage response = await client.GetAsync(uploadHref);

		Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[TestMethod]
	public async Task Download_UnknownUpstream_IsNotFound()
	{
		await using ProxyFixture fixture = await ProxyFixture.StartAsync();
		using HttpClient client = fixture.Client;

		using StringContent body = BatchRequest("download", new string('d', 64), 10);

		using HttpResponseMessage response = await client.PostAsync(
			"/gitlab/owner/repo.git/info/lfs/objects/batch",
			body);

		Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
	}

	[TestMethod]
	public async Task Download_RangeRequestOnAMiss_IsStreamedWithoutStoring()
	{
		await using ProxyFixture fixture = await ProxyFixture.StartAsync();
		(byte[] content, string oid) = Object("range on a miss");
		fixture.Upstream.AddObject(oid, content);

		JsonNode batch = await PostBatchAsync(fixture, "download", oid, content.Length);
		using HttpClient client = fixture.Client;

		using HttpRequestMessage request = new(HttpMethod.Get, Relative(HrefOf(batch, "download")));
		request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 3);

		using HttpResponseMessage response = await client.SendAsync(request);

		Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
		Assert.IsFalse(
			fixture.Store.Exists("github", oid),
			"A partial response must never be published as a whole object.");
		Assert.AreEqual("bytes=0-3", fixture.Upstream.Requests.Last().Range);
	}

	[TestMethod]
	public async Task Download_ObjectUpstreamDoesNotHave_ReportsTheErrorPerObject()
	{
		await using ProxyFixture fixture = await ProxyFixture.StartAsync();
		string missing = new('e', 64);

		JsonNode batch = await PostBatchAsync(fixture, "download", missing, 10);
		JsonNode entry = batch["objects"]![0]!;

		Assert.AreEqual(404, entry["error"]!["code"]!.GetValue<int>());
		Assert.IsFalse(entry.AsObject().ContainsKey("actions"));
	}

	[TestMethod]
	public async Task Upload_IsRelayedUpstreamAndStoredOnTheWayThrough()
	{
		await using ProxyFixture fixture = await ProxyFixture.StartAsync();
		(byte[] content, string oid) = Object("pushed object");

		JsonNode batch = await PostBatchAsync(fixture, "upload", oid, content.Length);
		using HttpClient client = fixture.Client;

		using ByteArrayContent body = new(content);

		using HttpResponseMessage response = await client.PutAsync(Relative(HrefOf(batch, "upload")), body);

		Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
		CollectionAssert.AreEqual(content, fixture.Upstream.Uploaded[oid]);
		Assert.IsTrue(fixture.Store.Exists("github", oid), "A pushed object should be cached for the next fetch.");
	}

	[TestMethod]
	public async Task Upload_ThenDownload_IsServedFromTheStoreWithoutFetchingUpstream()
	{
		await using ProxyFixture fixture = await ProxyFixture.StartAsync();
		(byte[] content, string oid) = Object("push then pull");

		JsonNode uploadBatch = await PostBatchAsync(fixture, "upload", oid, content.Length);
		using HttpClient client = fixture.Client;
		using ByteArrayContent pushBody = new(content);
		using HttpResponseMessage pushResponse = await client.PutAsync(
			Relative(HrefOf(uploadBatch, "upload")),
			pushBody);

		// Upstream now knows the object too, so a download batch returns a real action for it.
		fixture.Upstream.AddObject(oid, content);
		JsonNode downloadBatch = await PostBatchAsync(fixture, "download", oid, content.Length);
		byte[] fetched = await client.GetByteArrayAsync(Relative(HrefOf(downloadBatch, "download")));

		CollectionAssert.AreEqual(content, fetched);
		Assert.AreEqual(0, fixture.Upstream.FetchCount(oid), "The store-on-write copy should have served this.");
	}

	[TestMethod]
	public async Task Upload_UpstreamRejects_IsNotCached()
	{
		await using ProxyFixture fixture = await ProxyFixture.StartAsync();
		fixture.Upstream.UploadStatus = HttpStatusCode.Forbidden;
		(byte[] content, string oid) = Object("rejected push");

		JsonNode batch = await PostBatchAsync(fixture, "upload", oid, content.Length);
		using HttpClient client = fixture.Client;

		using ByteArrayContent body = new(content);

		using HttpResponseMessage response = await client.PutAsync(Relative(HrefOf(batch, "upload")), body);

		Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
		Assert.IsFalse(
			fixture.Store.Exists("github", oid),
			"Caching an upload upstream refused would serve bytes the real remote does not have.");
	}

	[TestMethod]
	public async Task Verify_IsRelayedUpstream()
	{
		await using ProxyFixture fixture = await ProxyFixture.StartAsync();
		fixture.Upstream.IncludeVerifyAction = true;
		(byte[] content, string oid) = Object("verify me");

		JsonNode batch = await PostBatchAsync(fixture, "upload", oid, content.Length);
		string verifyHref = Relative(HrefOf(batch, "verify"));
		using HttpClient client = fixture.Client;

		using StringContent body = new($$"""{"oid":"{{oid}}","size":{{content.Length}}}""");

		using HttpResponseMessage response = await client.PostAsync(verifyHref, body);

		Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
		Assert.EndsWith("/verify", fixture.Upstream.Requests.Last().Path);
	}

	[TestMethod]
	public async Task Verify_ActionPointsAtTheProxyNotUpstream()
	{
		await using ProxyFixture fixture = await ProxyFixture.StartAsync();
		fixture.Upstream.IncludeVerifyAction = true;
		(byte[] content, string oid) = Object("client needs no upstream access");

		JsonNode batch = await PostBatchAsync(fixture, "upload", oid, content.Length);

		Assert.StartsWith("https://cache.example/", HrefOf(batch, "verify"));
		Assert.DoesNotContain("upstream-verify-secret", batch.ToJsonString());
	}

	[TestMethod]
	public async Task UnrecognizedPath_IsRelayedUpstream()
	{
		await using ProxyFixture fixture = await ProxyFixture.StartAsync();
		using HttpClient client = fixture.Client;

		using StringContent body = new("{}");

		using HttpResponseMessage response = await client.PostAsync($"{LfsPath}/locks/verify", body);

		// A Git LFS feature the proxy does not model degrades to plain proxying.
		Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
		Assert.Contains("relayed", await response.Content.ReadAsStringAsync());
		Assert.EndsWith("/locks/verify", fixture.Upstream.Requests.Last().Path);
	}

	[TestMethod]
	public async Task HealthProbes_ReportLiveAndReady()
	{
		await using ProxyFixture fixture = await ProxyFixture.StartAsync();
		using HttpClient client = fixture.Client;

		using HttpResponseMessage live = await client.GetAsync("/healthz");
		using HttpResponseMessage ready = await client.GetAsync("/readyz");

		Assert.AreEqual(HttpStatusCode.OK, live.StatusCode);
		Assert.AreEqual(HttpStatusCode.OK, ready.StatusCode);
		Assert.AreEqual("ready", await ready.Content.ReadAsStringAsync());
	}

	[TestMethod]
	public async Task ConcurrentMissesForOneObject_ProduceASingleUpstreamFetch()
	{
		await using ProxyFixture fixture = await ProxyFixture.StartAsync();
		(byte[] content, string oid) = Object(new string('x', 200_000));
		fixture.Upstream.AddObject(oid, content);

		JsonNode batch = await PostBatchAsync(fixture, "download", oid, content.Length);
		string href = Relative(HrefOf(batch, "download"));
		using HttpClient client = fixture.Client;

		byte[][] bodies = await Task.WhenAll(
			Enumerable.Range(0, 8).Select(_ => client.GetByteArrayAsync(href)));

		foreach (byte[] body in bodies)
		{
			CollectionAssert.AreEqual(content, body);
		}

		// This is the property the whole cache exists for: eight clients, one upstream transfer.
		Assert.AreEqual(1, fixture.Upstream.FetchCount(oid));
	}
}
