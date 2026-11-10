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
	[DataRow(65536)]
	[DataRow(70000)]
	[DataRow(int.MaxValue)]
	[DataRow(-1)]
	[DataRow(int.MinValue)]
	public void Flag_OutOfRange_IsRefusedWithOneLineNamingTheFlag(int port)
	{
		// An out-of-range port used to pass the flag checks and abort startup with a Kestrel
		// ArgumentOutOfRangeException stack trace (issue #154).
		Assert.AreEqual($"--port must be between 0 and 65535, but was {port}.", Program.InvalidListenPortMessage(port));
	}

	[TestMethod]
	[DataRow(0)]
	[DataRow(8080)]
	[DataRow(65535)]
	public void Flag_InRange_IsAccepted(int port)
	{
		Assert.IsNull(Program.InvalidListenPortMessage(port));
	}

	[TestMethod]
	public void NoFlag_IsAccepted()
	{
		Assert.IsNull(Program.InvalidListenPortMessage(null));
	}

	[TestMethod]
	public async Task Flag_OutOfRange_FailsBeforeAServerStarts()
	{
		int exitCode = await Program.Main(["--port", "70000"]);

		Assert.AreEqual(1, exitCode);
	}

	[TestMethod]
	public void NoFlag_NoConfiguredEndpoint_DefaultsTo8080()
	{
		Assert.AreEqual("http://*:8080", ResolveEndpoint(null, []));
	}
}
