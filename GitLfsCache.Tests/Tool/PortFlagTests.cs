// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Tool;

using ktsu.GitLfsCache.Tool;
using Microsoft.Extensions.Configuration;

[TestClass]
public class PortFlagTests
{
	private static string? ResolveEndpoint(int? port, Dictionary<string, string?> configured)
	{
		IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(configured).Build();
		Dictionary<string, string?> overrides = [];

		Program.ApplyListenPort(configuration, overrides, port);

		// The overrides are layered over configuration, so this is the endpoint Kestrel ends up reading.
		return new ConfigurationBuilder()
			.AddInMemoryCollection(configured)
			.AddInMemoryCollection(overrides)
			.Build()[Program.HttpEndpointUrlKey];
	}

	[TestMethod]
	public void NoFlag_ConfiguredEndpoint_IsKept()
	{
		// The endpoint used to be overwritten with http://*:8080 whether or not --port was given (issue #140).
		string? endpoint = ResolveEndpoint(null, new() { [Program.HttpEndpointUrlKey] = "http://127.0.0.1:9123" });

		Assert.AreEqual("http://127.0.0.1:9123", endpoint);
	}

	[TestMethod]
	public void NoFlag_ConfiguredUrlsOrPorts_AddsNoEndpoint()
	{
		foreach (string key in (string[])["urls", "http_ports", "https_ports", "Kestrel:Endpoints:Https:Url"])
		{
			Assert.IsNull(ResolveEndpoint(null, new() { [key] = "http://127.0.0.1:9123" }), key);
		}
	}

	[TestMethod]
	public void Flag_OverridesTheConfiguredEndpoint()
	{
		string? endpoint = ResolveEndpoint(9000, new() { [Program.HttpEndpointUrlKey] = "http://127.0.0.1:9123" });

		Assert.AreEqual("http://*:9000", endpoint);
	}

	[TestMethod]
	public void NoFlag_NoConfiguredEndpoint_DefaultsTo8080()
	{
		Assert.AreEqual("http://*:8080", ResolveEndpoint(null, []));
	}
}
