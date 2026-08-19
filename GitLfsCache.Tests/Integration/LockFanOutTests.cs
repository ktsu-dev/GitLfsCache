// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Integration;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class LockFanOutTests
{
	private const string LfsPath = "/github/owner/repo.git/info/lfs";
	private const string Credential = "Basic dXNlcjp0b2tlbg==";

	private static async Task<HttpResponseMessage> PostBatchAsync(ProxyFixture fixture, string body)
	{
		using HttpClient client = fixture.Client;

		using HttpRequestMessage request = new(HttpMethod.Post, $"{LfsPath}/locks/batch")
		{
			Content = new StringContent(body, Encoding.UTF8, "application/vnd.git-lfs+json"),
		};

		request.Headers.TryAddWithoutValidation("Authorization", Credential);

		return await client.SendAsync(request);
	}

	private static async Task<JsonArray> ResultsOfAsync(ProxyFixture fixture, string body)
	{
		using HttpResponseMessage response = await PostBatchAsync(fixture, body);

		Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

		JsonNode parsed = (await response.Content.ReadFromJsonAsync<JsonNode>())!;
		return parsed["results"]!.AsArray();
	}

	private static string LockBody(params string[] paths) =>
		$$"""{"operation":"lock","paths":[{{string.Join(',', paths.Select(path => $"\"{path}\""))}}]}""";

	[TestMethod]
	public async Task ManyPaths_BecomeOneClientRoundTripAndManyUpstreamCalls()
	{
		// The whole point: git-lfs issues one request per path, one after another. A client that would
		// have paid fifty sequential round trips over a wide-area link pays one.
		await using ProxyFixture fixture = await ProxyFixture.StartAsync();

		string[] paths = [.. Enumerable.Range(0, 50).Select(index => $"Content/{index}.uasset")];

		JsonArray results = await ResultsOfAsync(fixture, LockBody(paths));

		Assert.HasCount(50, results);
		Assert.IsTrue(results.All(result => result!["ok"]!.GetValue<bool>()));
		Assert.AreEqual(50, fixture.Upstream.LockChangeRequests);
	}

	[TestMethod]
	public async Task EveryCall_CarriesTheCallersOwnCredential()
	{
		// What keeps this a parallelizer rather than a lock authority: upstream decides each creation
		// under the caller's identity, exactly as it would have done for git-lfs.
		await using ProxyFixture fixture = await ProxyFixture.StartAsync();

		await ResultsOfAsync(fixture, LockBody("a.uasset", "b.uasset"));

		Assert.IsTrue(
			fixture.Upstream.Requests
				.Where(request => request.Method == "POST" && request.Path.EndsWith("/locks", StringComparison.Ordinal))
				.All(request => request.Authorization == Credential),
			"every fanned-out call must carry the caller's credential");
	}

	[TestMethod]
	public async Task ResultsAreReturnedInTheOrderTheClientSentThem()
	{
		// They complete in whatever order upstream answers, and a client matching results to inputs by
		// position would otherwise lock the wrong files.
		await using ProxyFixture fixture = await ProxyFixture.StartAsync();

		string[] paths = [.. Enumerable.Range(0, 20).Select(index => $"{index}.uasset")];

		JsonArray results = await ResultsOfAsync(fixture, LockBody(paths));

		CollectionAssert.AreEqual(
			paths,
			results.Select(result => result!["path"]!.GetValue<string>()).ToArray());
	}

	[TestMethod]
	public async Task PartialFailure_IsReportedPerItemWithA200Overall()
	{
		// A single conflict must not discard the successful half.
		await using ProxyFixture fixture = await ProxyFixture.StartAsync();
		fixture.Upstream.LockChangeStatus = HttpStatusCode.Conflict;

		JsonArray results = await ResultsOfAsync(fixture, LockBody("a.uasset"));

		Assert.ContainsSingle(results);
		Assert.IsFalse(results[0]!["ok"]!.GetValue<bool>());
		Assert.AreEqual(409, results[0]!["status"]!.GetValue<int>());
		Assert.AreEqual("already locked", results[0]!["message"]!.GetValue<string>());
	}

	[TestMethod]
	public async Task Throttling_IsWaitedOutAndTheCallRetried()
	{
		// GitHub answers a secondary rate limit with Retry-After. Honouring it is what stops a fan-out
		// turning into a ban, and the item still has to succeed afterwards.
		await using ProxyFixture fixture = await ProxyFixture.StartAsync();
		fixture.Upstream.ThrottleLockChanges = 1;
		fixture.Upstream.ThrottleRetryAfterSeconds = 0;

		JsonArray results = await ResultsOfAsync(fixture, LockBody("a.uasset"));

		Assert.ContainsSingle(results);
		Assert.IsTrue(results[0]!["ok"]!.GetValue<bool>(), "the retry after a throttle must succeed");
		Assert.AreEqual(2, fixture.Upstream.LockChangeRequests, "one throttled attempt plus one retry");
	}

	[TestMethod]
	public async Task PersistentThrottling_GivesUpAndSaysSo()
	{
		await using ProxyFixture fixture = await ProxyFixture.StartAsync(
			new Dictionary<string, string?>(StringComparer.Ordinal)
			{
				["GitLfsCache:Locks:MaxFanOutRetries"] = "1",
			});

		fixture.Upstream.ThrottleLockChanges = 99;
		fixture.Upstream.ThrottleRetryAfterSeconds = 0;

		JsonArray results = await ResultsOfAsync(fixture, LockBody("a.uasset"));

		Assert.IsFalse(results[0]!["ok"]!.GetValue<bool>());
		Assert.AreEqual(429, results[0]!["status"]!.GetValue<int>());
		Assert.AreEqual(2, fixture.Upstream.LockChangeRequests, "the original attempt plus one retry");
	}

	[TestMethod]
	public async Task OversizedRequest_IsRefusedRatherThanPartlyRun()
	{
		await using ProxyFixture fixture = await ProxyFixture.StartAsync(
			new Dictionary<string, string?>(StringComparer.Ordinal)
			{
				["GitLfsCache:Locks:MaxFanOutPaths"] = "2",
			});

		using HttpResponseMessage response = await PostBatchAsync(
			fixture,
			LockBody("a", "b", "c"));

		Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
		Assert.AreEqual(0, fixture.Upstream.LockChangeRequests, "nothing may run before the refusal");
	}

	[TestMethod]
	[DataRow("""{"operation":"sideways","paths":["a"]}""")]
	[DataRow("""{"operation":"lock"}""")]
	[DataRow("""{"operation":"lock","paths":[]}""")]
	[DataRow("""{"operation":"lock","paths":[42]}""")]
	[DataRow("not json at all")]
	[DataRow("""{"operation":"unlock","paths":"not an array","ids":["1"]}""")]
	public async Task MalformedRequest_IsRefusedRatherThanPartlyHonoured(string body)
	{
		// The last case is the one worth spelling out: a "paths" that is present but not an array must
		// refuse the whole body rather than be skipped in favour of the "ids" beside it, or the client
		// gets a fan-out over half of what it asked for and no way to tell.
		await using ProxyFixture fixture = await ProxyFixture.StartAsync();

		using HttpResponseMessage response = await PostBatchAsync(fixture, body);

		Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.AreEqual(0, fixture.Upstream.LockChangeRequests);
	}

	[TestMethod]
	public async Task SuccessfulFanOut_InvalidatesTheSnapshot()
	{
		await using ProxyFixture fixture = await ProxyFixture.StartAsync();
		fixture.Upstream.Locks.Add("a");

		using (HttpClient client = fixture.Client)
		{
			using HttpRequestMessage listing = new(HttpMethod.Get, $"{LfsPath}/locks");
			listing.Headers.TryAddWithoutValidation("Authorization", Credential);
			using HttpResponseMessage first = await client.SendAsync(listing);
			Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
		}

		fixture.Upstream.Locks.Add("b");
		await ResultsOfAsync(fixture, LockBody("b"));

		using (HttpClient client = fixture.Client)
		{
			using HttpRequestMessage listing = new(HttpMethod.Get, $"{LfsPath}/locks");
			listing.Headers.TryAddWithoutValidation("Authorization", Credential);
			using HttpResponseMessage second = await client.SendAsync(listing);

			JsonNode parsed = (await second.Content.ReadFromJsonAsync<JsonNode>())!;
			Assert.HasCount(2, parsed["locks"]!.AsArray());
		}
	}

	[TestMethod]
	public async Task LocksDisabled_RefusesTheExtensionRatherThanRelayingIt()
	{
		// Upstream has no such endpoint, so relaying would turn a disabled feature into a confusing
		// 404 from the forge instead of a clear one from here.
		await using ProxyFixture fixture = await ProxyFixture.StartAsync(
			new Dictionary<string, string?>(StringComparer.Ordinal)
			{
				["GitLfsCache:Locks:Enabled"] = "false",
			});

		using HttpResponseMessage response = await PostBatchAsync(fixture, LockBody("a"));

		Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
		Assert.IsEmpty(fixture.Upstream.Requests);
	}
}
