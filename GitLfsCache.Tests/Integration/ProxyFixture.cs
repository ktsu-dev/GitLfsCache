// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Integration;

using System.IO.Abstractions;
using ktsu.GitLfsCache.Storage;
using ktsu.GitLfsCache.Upstreams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Testably.Abstractions.Testing;

/// <summary>
/// Builds a real in-process proxy over a mock filesystem and a stub upstream.
/// </summary>
/// <remarks>
/// The proxy is assembled through its own <c>AddGitLfsCache</c> and <c>MapGitLfsCache</c> extensions
/// rather than by hand, so these tests exercise the wiring a host actually gets, including the startup
/// check and the options validator. Exactly two things are swapped: the filesystem, so no test touches
/// a disk, and the upstream transport, so no test touches a network.
/// </remarks>
internal sealed class ProxyFixture : IAsyncDisposable
{
	private readonly IHost _host;

	private ProxyFixture(IHost host, StubUpstream upstream, MockFileSystem fileSystem)
	{
		_host = host;
		Upstream = upstream;
		FileSystem = fileSystem;
	}

	/// <summary>Gets the stub upstream the proxy talks to.</summary>
	public StubUpstream Upstream { get; }

	/// <summary>Gets the in-memory filesystem the store is written to.</summary>
	public MockFileSystem FileSystem { get; }

	/// <summary>Gets a client addressed at the proxy.</summary>
	public HttpClient Client => _host.GetTestClient();

	/// <summary>
	/// Gets the test server, for the few tests that must build the request context themselves.
	/// </summary>
	/// <remarks>
	/// Going through <see cref="Client"/> means going through <see cref="Uri"/>, which canonicalizes
	/// the path. A test that needs to present a path <see cref="Uri"/> would have rewritten has to
	/// set it on the context directly.
	/// </remarks>
	public TestServer Server => _host.GetTestServer();

	/// <summary>Gets the store the proxy is using.</summary>
	public IObjectStore Store => _host.Services.GetRequiredService<IObjectStore>();

	/// <summary>Gets the absolute store root, valid on whichever platform the suite runs on.</summary>
	public static string StoreRoot { get; } = Path.Combine(
		Path.GetPathRoot(Path.GetTempPath()) ?? Path.DirectorySeparatorChar.ToString(),
		"gitlfscache-integration");

	/// <summary>
	/// Starts a proxy.
	/// </summary>
	/// <param name="settings">Extra configuration values, overriding the defaults.</param>
	/// <returns>The running fixture.</returns>
	[System.Diagnostics.CodeAnalysis.SuppressMessage(
		"Maintainability",
		"CA1506:Avoid excessive class coupling",
		Justification = "Assembling a host is coupled to the host, routing, configuration and proxy types by nature. Splitting it up would scatter the wiring these tests exist to exercise.")]
	public static async Task<ProxyFixture> StartAsync(Dictionary<string, string?>? settings = null)
	{
		StubUpstream upstream = new();
		MockFileSystem fileSystem = new();
		fileSystem.Directory.CreateDirectory(StoreRoot);

		Dictionary<string, string?> configuration = new(StringComparer.Ordinal)
		{
			["GitLfsCache:PublicBaseUrl"] = "https://cache.example",
			["GitLfsCache:TokenKeys:0"] = Convert.ToBase64String(new byte[32]),
			["GitLfsCache:TokenLifetime"] = "01:00:00",
			["GitLfsCache:Store:Root"] = StoreRoot,
			["GitLfsCache:Store:MaxSize"] = "1GB",
			["GitLfsCache:Store:LowWaterMark"] = "0.9",
			["GitLfsCache:Store:StagingMaxAge"] = "06:00:00",
			["GitLfsCache:Store:MaintenanceInterval"] = "01:00:00",
			["GitLfsCache:Fetch:FollowerTimeout"] = "00:00:02",
			["GitLfsCache:Upstreams:github:BaseUrl"] = "https://upstream.example",
			["GitLfsCache:Upstreams:github:Repositories:0"] = "**",
		};

		foreach ((string key, string? value) in settings ?? [])
		{
			configuration[key] = value;
		}

		IHost host = await new HostBuilder()
			.ConfigureAppConfiguration(builder => builder.AddInMemoryCollection(configuration))
			.ConfigureWebHost(webHost => webHost
				.UseTestServer()
				.ConfigureServices((context, services) =>
				{
					services.AddRouting();
					services.AddGitLfsCache(context.Configuration);

					services.RemoveAll<IFileSystem>();
					services.AddSingleton<IFileSystem>(fileSystem);

					// Configured by name rather than by re-registering the typed client, so the
					// registration AddGitLfsCache made is the one that gets the stub transport.
					services.AddHttpClient(UpstreamClient.HttpClientName)
						.ConfigurePrimaryHttpMessageHandler(() => upstream);
				})
				.Configure(app =>
				{
					app.UseRouting();
					app.UseEndpoints(endpoints => endpoints.MapGitLfsCache());
				}))
			.StartAsync()
			.ConfigureAwait(false);

		return new ProxyFixture(host, upstream, fileSystem);
	}

	/// <inheritdoc />
	public async ValueTask DisposeAsync()
	{
		await _host.StopAsync().ConfigureAwait(false);
		_host.Dispose();
	}
}
