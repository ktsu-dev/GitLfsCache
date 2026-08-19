// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Integration;

using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Asserts that the repository allow-list is enforced on every route kind, before any upstream call.
/// </summary>
[TestClass]
public class RepositoryAllowListEnforcementTests
{
	private const string Oid =
		"0000000000000000000000000000000000000000000000000000000000000000";

	private static Dictionary<string, string?> AllowStudioOnly() => new(StringComparer.Ordinal)
	{
		["GitLfsCache:Upstreams:github:Repositories:0"] = "studio/**",
	};

	private static async Task<HttpResponseMessage> SendAsync(
		ProxyFixture fixture,
		HttpMethod method,
		string path)
	{
		using HttpClient client = fixture.Client;

		using HttpRequestMessage request = new(method, path);
		request.Headers.TryAddWithoutValidation("Authorization", "Basic dXNlcjp0b2tlbg==");

		if (method == HttpMethod.Post)
		{
			request.Content = new StringContent(
				"""{"operation":"download","transfers":["basic"],"objects":[],"hash_algo":"sha256"}""",
				Encoding.UTF8,
				"application/vnd.git-lfs+json");
		}

		return await client.SendAsync(request);
	}

	[TestMethod]
	[DataRow("POST", "/github/other/repo.git/info/lfs/objects/batch")]
	[DataRow("GET", "/github/other/repo.git/info/lfs/locks")]
	[DataRow("GET", "/github/other/repo.git/info/lfs/objects/" + Oid)]
	[DataRow("POST", "/github/other/repo.git/info/lfs/objects/" + Oid + "/verify")]
	public async Task UnlistedRepository_IsRefusedWithoutReachingUpstream(string method, string path)
	{
		await using ProxyFixture fixture = await ProxyFixture.StartAsync(AllowStudioOnly());

		using HttpResponseMessage response = await SendAsync(fixture, new HttpMethod(method), path);

		Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);

		// The point of checking before the upstream call: a refused path must cost upstream nothing
		// and must not let a caller learn anything from how long it took or whether it was reached.
		Assert.IsEmpty(fixture.Upstream.Requests);
	}

	[TestMethod]
	public async Task ListedRepository_ReachesUpstream()
	{
		await using ProxyFixture fixture = await ProxyFixture.StartAsync(AllowStudioOnly());

		using HttpResponseMessage response = await SendAsync(
			fixture,
			HttpMethod.Post,
			"/github/studio/game.git/info/lfs/objects/batch");

		Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
		Assert.ContainsSingle(fixture.Upstream.Requests);
	}

	[TestMethod]
	public async Task UnlistedRepository_IsRefusedTheSameWayAsAnUnknownUpstream()
	{
		// Both are 404 rather than one being 403, because distinguishing them would tell a caller
		// which repositories exist.
		await using ProxyFixture fixture = await ProxyFixture.StartAsync(AllowStudioOnly());

		using HttpResponseMessage unlisted = await SendAsync(
			fixture,
			HttpMethod.Post,
			"/github/other/repo.git/info/lfs/objects/batch");

		using HttpResponseMessage unknownUpstream = await SendAsync(
			fixture,
			HttpMethod.Post,
			"/gitlab/studio/game.git/info/lfs/objects/batch");

		Assert.AreEqual(unknownUpstream.StatusCode, unlisted.StatusCode);
	}
}
