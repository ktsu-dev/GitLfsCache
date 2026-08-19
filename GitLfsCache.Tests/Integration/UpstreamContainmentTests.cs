// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Integration;

using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Asserts that a request path can never move an upstream request above the configured base URL.
/// </summary>
/// <remarks>
/// A base URL carrying a path prefix is the documented shape for Azure DevOps
/// (<c>https://dev.azure.com/myorg</c>), so in practice that prefix is a tenancy boundary and has to
/// hold. The host cannot change however the path is written, because the combined string always
/// begins with the configured scheme and authority, so these tests are about the path prefix only.
/// <para>
/// The request context is built directly rather than driven through an <see cref="HttpClient"/>,
/// because <see cref="Uri"/> removes dot segments while building the request and a client therefore
/// cannot express the path under test. These tests state that <em>if</em> a path carrying dot
/// segments reaches the handler, the upstream request stays inside the configured base URL. Whether
/// a real server ever delivers such a path, which would mean percent encoded dot segments surviving
/// normalization and then being decoded into <c>Request.Path</c>, is a separate question about the
/// server and is not settled here.
/// </para>
/// </remarks>
[TestClass]
public class UpstreamContainmentTests
{
	private const string PathPrefix = "/myorg/";

	private const string EmptyBatch =
		"""{"operation":"download","transfers":["basic"],"objects":[],"hash_algo":"sha256"}""";

	private static Dictionary<string, string?> PrefixedUpstream() => new(StringComparer.Ordinal)
	{
		["GitLfsCache:Upstreams:github:BaseUrl"] = "https://upstream.example/myorg",
	};

	private static async Task SendAsync(
		ProxyFixture fixture,
		string method,
		string path,
		string? body = null)
	{
		await fixture.Server.SendAsync(context =>
		{
			context.Request.Method = method;
			context.Request.Path = path;
			context.Request.Headers.Authorization = "Basic dXNlcjp0b2tlbg==";

			if (body is not null)
			{
				byte[] bytes = Encoding.UTF8.GetBytes(body);
				context.Request.Body = new MemoryStream(bytes);
				context.Request.ContentLength = bytes.Length;
				context.Request.ContentType = "application/vnd.git-lfs+json";
			}
		});
	}

	/// <summary>
	/// Fails naming every upstream path that escaped the prefix. A request the proxy refused outright
	/// relays nothing and passes, because refusing is also containment.
	/// </summary>
	private static void AssertEveryUpstreamRequestStayedUnderThePrefix(ProxyFixture fixture)
	{
		string[] escaped = [.. fixture.Upstream.Requests
			.Select(request => request.Path)
			.Where(path => !path.StartsWith(PathPrefix, StringComparison.Ordinal))];

		Assert.IsEmpty(
			escaped,
			$"upstream requests escaped the configured path prefix: {string.Join(", ", escaped)}");
	}

	[TestMethod]
	public async Task Batch_WithDotSegments_StaysUnderTheUpstreamPathPrefix()
	{
		await using ProxyFixture fixture = await ProxyFixture.StartAsync(PrefixedUpstream());

		await SendAsync(
			fixture,
			"POST",
			"/github/owner/../../other/repo.git/info/lfs/objects/batch",
			EmptyBatch);

		AssertEveryUpstreamRequestStayedUnderThePrefix(fixture);
	}

	[TestMethod]
	public async Task Relay_WithDotSegments_StaysUnderTheUpstreamPathPrefix()
	{
		await using ProxyFixture fixture = await ProxyFixture.StartAsync(PrefixedUpstream());

		await SendAsync(fixture, "GET", "/github/owner/../../other/repo.git/info/lfs/locks");

		AssertEveryUpstreamRequestStayedUnderThePrefix(fixture);
	}

	[TestMethod]
	public async Task Batch_WithoutDotSegments_ReachesTheUpstreamUnderThePrefix()
	{
		await using ProxyFixture fixture = await ProxyFixture.StartAsync(PrefixedUpstream());

		await SendAsync(
			fixture,
			"POST",
			"/github/owner/repo.git/info/lfs/objects/batch",
			EmptyBatch);

		Assert.ContainsSingle(fixture.Upstream.Requests);
		Assert.AreEqual(
			"/myorg/owner/repo.git/info/lfs/objects/batch",
			fixture.Upstream.Requests[0].Path);
	}
}
