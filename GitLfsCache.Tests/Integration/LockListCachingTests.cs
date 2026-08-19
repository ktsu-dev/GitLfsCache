// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Integration;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class LockListCachingTests
{
	private const string LfsPath = "/github/owner/repo.git/info/lfs";
	private const string Credential = "Basic dXNlcjp0b2tlbg==";
	private const string OtherCredential = "Basic c29tZW9uZTplbHNl";

	private static readonly string[] FivePaths = ["a", "b", "c", "d", "e"];
	private static readonly string[] OnlyB = ["Content/b.uasset"];

	private static async Task<JsonNode> ListLocksAsync(
		ProxyFixture fixture,
		string query = "",
		string? authorization = Credential,
		HttpStatusCode expected = HttpStatusCode.OK)
	{
		using HttpClient client = fixture.Client;
		using HttpRequestMessage request = new(HttpMethod.Get, $"{LfsPath}/locks{query}");

		if (authorization is not null)
		{
			request.Headers.TryAddWithoutValidation("Authorization", authorization);
		}

		using HttpResponseMessage response = await client.SendAsync(request);

		Assert.AreEqual(expected, response.StatusCode);
		return (await response.Content.ReadFromJsonAsync<JsonNode>())!;
	}

	private static string[] PathsOf(JsonNode listing) =>
		[.. listing["locks"]!.AsArray().Select(entry => entry!["path"]!.GetValue<string>())];

	[TestMethod]
	public async Task ManyClients_ProduceOneUpstreamWalk()
	{
		// The headline claim of the whole subsystem: lock traffic upstream stops scaling with the
		// number of clients.
		await using ProxyFixture fixture = await ProxyFixture.StartAsync();
		fixture.Upstream.Locks.AddRange(["Content/a.uasset", "Content/b.uasset"]);

		for (int client = 0; client < 5; client++)
		{
			JsonNode listing = await ListLocksAsync(fixture);
			Assert.HasCount(2, listing["locks"]!.AsArray());
		}

		// One upstream call in total. The first request walks the listing and, by succeeding, admits its
		// own credential; the remaining four are answered entirely from the snapshot. A client polling
		// every 30 seconds therefore costs upstream one walk per listing lifetime no matter how many
		// editors are open, which is the entire point of the subsystem.
		Assert.AreEqual(1, fixture.Upstream.LockPageRequests);
	}

	[TestMethod]
	public async Task ADifferentCredential_CostsOneProbeRatherThanAnotherWalk()
	{
		// The saving for a second client is the difference between one page and every page. It is
		// invisible on a two-lock repository and is the whole story on one with thousands.
		await using ProxyFixture fixture = await ProxyFixture.StartAsync();
		fixture.Upstream.LocksPageSize = 1;
		fixture.Upstream.Locks.AddRange(FivePaths);

		await ListLocksAsync(fixture, authorization: Credential);
		int afterFirstClient = fixture.Upstream.LockPageRequests;

		await ListLocksAsync(fixture, authorization: OtherCredential);

		Assert.AreEqual(5, afterFirstClient, "the first client walks every page");
		Assert.AreEqual(
			afterFirstClient + 1,
			fixture.Upstream.LockPageRequests,
			"the second client proves its credential with a single page and reads the snapshot");
	}

	[TestMethod]
	public async Task PaginatedUpstream_IsWalkedAndServedInOneResponse()
	{
		// The reason this is worth doing at all: a client asking once gets everything, instead of
		// walking cursors itself over a wide-area link.
		await using ProxyFixture fixture = await ProxyFixture.StartAsync();
		fixture.Upstream.LocksPageSize = 2;
		fixture.Upstream.Locks.AddRange(["a", "b", "c", "d", "e"]);

		JsonNode listing = await ListLocksAsync(fixture);

		CollectionAssert.AreEqual(FivePaths, PathsOf(listing));
		Assert.IsNull(listing["next_cursor"]);
	}

	[TestMethod]
	public async Task UnadmittedCredential_IsProvedAgainstUpstreamBeforeBeingServed()
	{
		// A second client must not be served from a snapshot the first client's credential produced.
		await using ProxyFixture fixture = await ProxyFixture.StartAsync();
		fixture.Upstream.Locks.Add("a");

		await ListLocksAsync(fixture, authorization: Credential);

		int before = fixture.Upstream.Requests.Count;
		await ListLocksAsync(fixture, authorization: OtherCredential);

		Assert.IsTrue(
			fixture.Upstream.Requests.Count > before,
			"a credential that has never been proved must reach upstream before being served");
	}

	[TestMethod]
	public async Task UpstreamRefusal_IsReturnedAndNothingIsServed()
	{
		await using ProxyFixture fixture = await ProxyFixture.StartAsync();
		fixture.Upstream.Locks.Add("a");
		fixture.Upstream.LocksStatus = HttpStatusCode.Forbidden;

		using HttpClient client = fixture.Client;
		using HttpRequestMessage request = new(HttpMethod.Get, $"{LfsPath}/locks");
		request.Headers.TryAddWithoutValidation("Authorization", Credential);

		using HttpResponseMessage response = await client.SendAsync(request);

		Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[TestMethod]
	public async Task RefusedCredential_IsNotServedFromAnExistingSnapshot()
	{
		// The failure that matters most: one client warms the cache, then a credential upstream
		// refuses must still be refused rather than handed the warm snapshot.
		await using ProxyFixture fixture = await ProxyFixture.StartAsync();
		fixture.Upstream.Locks.Add("a");

		await ListLocksAsync(fixture, authorization: Credential);

		fixture.Upstream.LocksStatus = HttpStatusCode.Forbidden;

		using HttpClient client = fixture.Client;
		using HttpRequestMessage request = new(HttpMethod.Get, $"{LfsPath}/locks");
		request.Headers.TryAddWithoutValidation("Authorization", OtherCredential);

		using HttpResponseMessage response = await client.SendAsync(request);

		Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[TestMethod]
	public async Task PathFilter_IsAppliedToTheSnapshot()
	{
		await using ProxyFixture fixture = await ProxyFixture.StartAsync();
		fixture.Upstream.Locks.AddRange(["Content/a.uasset", "Content/b.uasset"]);

		JsonNode listing = await ListLocksAsync(fixture, "?path=Content/b.uasset");

		CollectionAssert.AreEqual(OnlyB, PathsOf(listing));
	}

	[TestMethod]
	public async Task LimitAndCursor_WalkTheSnapshotExactlyOnce()
	{
		await using ProxyFixture fixture = await ProxyFixture.StartAsync();
		fixture.Upstream.Locks.AddRange(["a", "b", "c", "d", "e"]);

		List<string> seen = [];
		string query = "?limit=2";

		while (true)
		{
			JsonNode page = await ListLocksAsync(fixture, query);
			seen.AddRange(PathsOf(page));

			if (page["next_cursor"]?.GetValue<string>() is not string cursor)
			{
				break;
			}

			query = $"?limit=2&cursor={Uri.EscapeDataString(cursor)}";
		}

		CollectionAssert.AreEqual(FivePaths, seen);
	}

	[TestMethod]
	public async Task CreatingALock_InvalidatesTheSnapshot()
	{
		await using ProxyFixture fixture = await ProxyFixture.StartAsync();
		fixture.Upstream.Locks.Add("a");

		Assert.HasCount(1, (await ListLocksAsync(fixture))["locks"]!.AsArray());

		// A lock taken through the proxy has to be visible to the next listing rather than waiting out
		// the lifetime, because the client that took it looks immediately.
		fixture.Upstream.Locks.Add("b");

		using (HttpClient client = fixture.Client)
		{
			using HttpRequestMessage create = new(HttpMethod.Post, $"{LfsPath}/locks")
			{
				Content = new StringContent("""{"path":"b"}""", Encoding.UTF8, "application/vnd.git-lfs+json"),
			};

			create.Headers.TryAddWithoutValidation("Authorization", Credential);
			using HttpResponseMessage created = await client.SendAsync(create);
			Assert.AreEqual(HttpStatusCode.OK, created.StatusCode);
		}

		Assert.HasCount(2, (await ListLocksAsync(fixture))["locks"]!.AsArray());
	}

	[TestMethod]
	public async Task LocksDisabled_RelaysExactlyAsBefore()
	{
		// The fallback if the cache is ever suspected of being wrong.
		await using ProxyFixture fixture = await ProxyFixture.StartAsync(new Dictionary<string, string?>(StringComparer.Ordinal)
		{
			["GitLfsCache:Locks:Enabled"] = "false",
		});

		fixture.Upstream.Locks.Add("a");

		await ListLocksAsync(fixture);
		await ListLocksAsync(fixture);

		// Two requests, two upstream calls: nothing is being cached.
		Assert.AreEqual(2, fixture.Upstream.LockPageRequests);
	}
}
