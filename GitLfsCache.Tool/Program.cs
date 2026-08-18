// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tool;

using System.CommandLine;
using System.Security.Cryptography;
using ktsu.Essentials;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

/// <summary>
/// Entry point for the gitlfscache host.
/// </summary>
/// <remarks>
/// This same binary is the dotnet tool payload and the container entrypoint, so the two cannot drift.
/// The flags exist for the tool: nobody running this on their own machine should have to type
/// <c>--GitLfsCache:Store:Root=</c>. In a container every value normally arrives through environment
/// variables instead, so every flag is optional and only overrides what configuration already holds.
/// </remarks>
internal static class Program
{
	private static async Task<int> Main(string[] args)
	{
		Option<int?> port = new("--port", "-p")
		{
			Description = "Port to listen on. Defaults to 8080.",
		};

		Option<string?> store = new("--store", "-s")
		{
			Description = "Directory to cache objects in. Defaults to a per-user application data directory.",
		};

		Option<string?> maxSize = new("--max-size")
		{
			Description = "Cache byte budget, for example 500GB or 500Gi.",
		};

		Option<string?> tokenKey = new("--token-key")
		{
			Description =
				"Base64 encoded 32 byte key protecting rewritten transfer URLs. Generated for this run when omitted.",
		};

		Option<string?> publicBaseUrl = new("--public-base-url")
		{
			Description =
				"Externally reachable base URL to build transfer URLs from. Derived from each request when omitted.",
		};

		Option<string[]> upstreams = new("--upstream", "-u")
		{
			Description = "An upstream as name=url, for example github=https://github.com. Repeatable.",
			AllowMultipleArgumentsPerToken = false,
		};

		RootCommand root = new("A caching reverse proxy for the Git LFS HTTP API.")
		{
			port,
			store,
			maxSize,
			tokenKey,
			publicBaseUrl,
			upstreams,
		};

		root.SetAction(async (parseResult, cancellationToken) =>
		{
			Dictionary<string, string?> overrides = [];
			int listenPort = parseResult.GetValue(port) ?? 8080;

			if (parseResult.GetValue(store) is string storeRoot)
			{
				overrides["GitLfsCache:Store:Root"] = storeRoot;
			}

			if (parseResult.GetValue(maxSize) is string budget)
			{
				overrides["GitLfsCache:Store:MaxSize"] = budget;
			}

			if (parseResult.GetValue(publicBaseUrl) is string baseUrl)
			{
				overrides["GitLfsCache:PublicBaseUrl"] = baseUrl;
			}

			if (parseResult.GetValue(tokenKey) is string key)
			{
				overrides["GitLfsCache:TokenKeys:0"] = key;
			}

			string[] configuredUpstreams = parseResult.GetValue(upstreams) ?? [];

			foreach (string entry in configuredUpstreams)
			{
				int separator = entry.IndexOf('=', StringComparison.Ordinal);

				if (separator <= 0 || separator == entry.Length - 1)
				{
					await Console.Error
						.WriteLineAsync(
							$"'{entry}' is not a valid upstream. Use name=url, for example github=https://github.com.")
						.ConfigureAwait(false);
					return 1;
				}

				overrides[$"GitLfsCache:Upstreams:{entry[..separator]}:BaseUrl"] = entry[(separator + 1)..];
			}

			return await RunAsync(overrides, listenPort, cancellationToken).ConfigureAwait(false);
		});

		return await root.Parse(args)
			.InvokeAsync(configuration: null, cancellationToken: CancellationToken.None)
			.ConfigureAwait(false);
	}

	private static async Task<int> RunAsync(
		Dictionary<string, string?> overrides,
		int port,
		CancellationToken cancellationToken)
	{
		WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			// The command line is parsed by System.CommandLine, not by the configuration provider, so
			// the friendly flags above are not also read as configuration keys.
			Args = [],
			ApplicationName = "ktsu.GitLfsCache",
		});

		ApplyDefaults(builder.Configuration, overrides);
		builder.Configuration.AddInMemoryCollection(overrides);
		builder.Configuration["Kestrel:Endpoints:Http:Url"] = $"http://*:{port}";

		builder.Services.AddGitLfsCache(builder.Configuration);

		// Behind an ingress the request the proxy sees is not the URL the client used, so the scheme
		// and host from the forwarded headers are what make derived transfer URLs correct. Without
		// KnownNetworks cleared the middleware ignores headers from a proxy it was not told about,
		// which inside a cluster is every proxy.
		builder.Services.Configure<ForwardedHeadersOptions>(options =>
		{
			options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
				| ForwardedHeaders.XForwardedProto
				| ForwardedHeaders.XForwardedHost;
			options.KnownIPNetworks.Clear();
			options.KnownProxies.Clear();
		});

		WebApplication app = builder.Build();

		app.UseForwardedHeaders();
		app.MapGitLfsCache();

		await app.RunAsync(cancellationToken).ConfigureAwait(false);
		return 0;
	}

	/// <summary>
	/// Fills in the values a local run should not have to supply.
	/// </summary>
	/// <remarks>
	/// An ephemeral token key is generated when none is configured. That is correct for a single local
	/// process and wrong for anything else, so it warns: outstanding transfer URLs stop working when the
	/// process restarts, and a second replica cannot serve URLs the first one issued.
	/// </remarks>
	private static void ApplyDefaults(ConfigurationManager configuration, Dictionary<string, string?> overrides)
	{
		bool hasStore = overrides.ContainsKey("GitLfsCache:Store:Root")
			|| !string.IsNullOrWhiteSpace(configuration["GitLfsCache:Store:Root"]);

		if (!hasStore)
		{
			overrides["GitLfsCache:Store:Root"] =
				UserDirectories.GetApplicationDataDirectory("ktsu", "GitLfsCache");
		}

		bool hasKey = overrides.ContainsKey("GitLfsCache:TokenKeys:0")
			|| !string.IsNullOrWhiteSpace(configuration["GitLfsCache:TokenKeys:0"]);

		if (!hasKey)
		{
			overrides["GitLfsCache:TokenKeys:0"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

			Console.Error.WriteLine(
				"warning: no --token-key given, so a key was generated for this run. Transfer URLs already "
				+ "handed out will stop working when this process restarts, and a second instance cannot "
				+ "serve them. Set a key explicitly for anything beyond a single local process.");
		}
	}
}
