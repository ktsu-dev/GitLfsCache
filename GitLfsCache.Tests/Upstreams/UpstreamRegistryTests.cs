// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Upstreams;

using ktsu.GitLfsCache.Configuration;
using ktsu.GitLfsCache.Upstreams;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class UpstreamRegistryTests
{
	private static UpstreamRegistry Registry()
	{
		GitLfsCacheOptions options = new();
		options.Upstreams["github"] = new UpstreamOptions { BaseUrl = new Uri("https://github.com") };
		options.Upstreams["ado"] = new UpstreamOptions { BaseUrl = new Uri("https://dev.azure.com/org") };
		return new UpstreamRegistry(Options.Create(options));
	}

	[TestMethod]
	public void TryResolve_KnownKey_ReturnsBaseUrl()
	{
		Assert.IsTrue(Registry().TryResolve("github", out Uri? baseUrl));
		Assert.AreEqual(new Uri("https://github.com"), baseUrl);
	}

	[TestMethod]
	public void TryResolve_KeyCasingDiffers_StillResolves()
	{
		Assert.IsTrue(Registry().TryResolve("GitHub", out Uri? baseUrl));
		Assert.AreEqual(new Uri("https://github.com"), baseUrl);
	}

	[TestMethod]
	public void TryResolve_UnknownKey_ReturnsFalseAndNull()
	{
		Assert.IsFalse(Registry().TryResolve("gitlab", out Uri? baseUrl));
		Assert.IsNull(baseUrl);
	}

	[TestMethod]
	public void TryResolve_KeyWithPathSegment_DoesNotResolve()
	{
		Assert.IsFalse(Registry().TryResolve("github/owner", out Uri? _));
	}

	[TestMethod]
	public void TryResolve_EmptyKey_DoesNotResolve()
	{
		Assert.IsFalse(Registry().TryResolve(string.Empty, out Uri? _));
	}
}
