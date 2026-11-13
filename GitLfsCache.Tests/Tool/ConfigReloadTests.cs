// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Tool;

using System.Net;
using System.Net.Sockets;
using ktsu.GitLfsCache.Tool;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

[TestClass]
public class ConfigReloadTests
{
	[TestMethod]
	public void HostOptions_DefaultAppSettingsSources_DoNotReloadOnChange()
	{
		// Reloading appsettings.json on change watches the whole content root, the working directory,
		// which on Linux took an inotify watch per directory beneath it and could use up the user's limit
		// (issue #166).
		WebApplicationBuilder builder = WebApplication.CreateBuilder(Program.HostOptions());

		FileConfigurationSource[] fileSources = [.. builder.Configuration.Sources.OfType<FileConfigurationSource>()];

		Assert.AreNotEqual(0, fileSources.Length, "Precondition: the builder adds its default appsettings.json sources.");
		foreach (FileConfigurationSource source in fileSources)
		{
			Assert.IsFalse(source.ReloadOnChange, $"{source.Path} should not reload on change.");
		}
	}

	[TestMethod]
	public void HostOptions_StillReadsAppSettingsFromTheWorkingDirectory()
	{
		// Turning reload off must not stop the working directory's appsettings.json being read.
		string contentRoot = Directory.CreateTempSubdirectory("gitlfscache-config-").FullName;
		try
		{
			File.WriteAllText(Path.Combine(contentRoot, "appsettings.json"), """{ "GitLfsCache": { "PublicBaseUrl": "https://lfs.example" } }""");
			WebApplicationOptions options = Program.HostOptions();
			WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
			{
				Args = options.Args,
				ApplicationName = options.ApplicationName,
				ContentRootPath = contentRoot,
			});

			Assert.AreEqual("https://lfs.example", builder.Configuration["GitLfsCache:PublicBaseUrl"]);
		}
		finally
		{
			Directory.Delete(contentRoot, recursive: true);
		}
	}

	[TestMethod]
	[Timeout(60_000, CooperativeCancellation = true)]
	public async Task RunAsync_WithHostOptions_StartsServesAndStops()
	{
		// The tool's own host, built from HostOptions, still starts and serves with reload turned off.
		string store = Directory.CreateTempSubdirectory("gitlfscache-run-").FullName;
		try
		{
			int port = FreePort();
			Dictionary<string, string?> overrides = new()
			{
				["GitLfsCache:Store:Root"] = store,
				["GitLfsCache:Upstreams:github:BaseUrl"] = "https://github.com",
			};
			Dictionary<string, List<string>> allowLists = new() { ["github"] = ["studio/**"] };
			using CancellationTokenSource stop = new();

			Task<int> run = Program.RunAsync(overrides, allowLists, port, configPath: null, stop.Token);

			using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(5) };
			bool served = false;
			while (!served && !run.IsCompleted)
			{
				try
				{
					using HttpResponseMessage response = await client.GetAsync(new Uri($"http://127.0.0.1:{port}/"));
					served = true;
				}
				catch (HttpRequestException)
				{
					await Task.Delay(100);
				}
			}

			Assert.IsFalse(run.IsCompleted, "The host should still be running until it is stopped.");
			await stop.CancelAsync();
			Assert.AreEqual(0, await run);
		}
		finally
		{
			Directory.Delete(store, recursive: true);
		}
	}

	private static int FreePort()
	{
		using TcpListener listener = new(IPAddress.Loopback, 0);
		listener.Start();
		return ((IPEndPoint)listener.LocalEndpoint).Port;
	}
}
