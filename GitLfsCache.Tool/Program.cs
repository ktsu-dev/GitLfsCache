// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tool;

using System.CommandLine;
using System.Security.Cryptography;
using ktsu.Essentials;
using ktsu.GitLfsCache.Configuration;
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
	/// <summary>The configuration key the <c>--store</c> flag overrides.</summary>
	private const string StoreRootKey = "GitLfsCache:Store:Root";

	/// <summary>The configuration key the <c>--token-key</c> flag overrides.</summary>
	private const string TokenKeyKey = "GitLfsCache:TokenKeys:0";

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

		Option<string?> configFile = new("--config", "-c")
		{
			Description =
				"Path to a JSON configuration file to load. Layered over any appsettings.json in the working directory, and still overridden by the flags below.",
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

		Option<string[]> allow = new("--allow", "-a")
		{
			Description =
				"A repository path pattern an upstream may serve, as name=pattern, for example github=studio/**. Repeatable. Required at least once per upstream.",
			AllowMultipleArgumentsPerToken = false,
		};

		RootCommand root = new("A caching reverse proxy for the Git LFS HTTP API.")
		{
			port,
			configFile,
			store,
			maxSize,
			tokenKey,
			publicBaseUrl,
			upstreams,
			allow,
		};

		root.SetAction(async (parseResult, cancellationToken) =>
		{
			Dictionary<string, string?> overrides = [];
			int listenPort = parseResult.GetValue(port) ?? 8080;
			string? configPath = null;

			if (parseResult.GetValue(configFile) is string requestedConfig)
			{
				configPath = Path.GetFullPath(requestedConfig);

				// Checked here rather than left to the configuration provider, which throws a
				// FileNotFoundException with a stack trace. Someone who mistyped a path should get one
				// line naming the path they gave.
				if (!File.Exists(configPath))
				{
					return await FailAsync($"No configuration file at '{configPath}'.").ConfigureAwait(false);
				}
			}

			if (parseResult.GetValue(store) is string storeRoot)
			{
				overrides[StoreRootKey] = ResolveStoreRoot(storeRoot);
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
				overrides[TokenKeyKey] = key;
			}

			if (!TryApplyUpstreams(parseResult.GetValue(upstreams), overrides, out string? invalidUpstream))
			{
				return await FailAsync(
					$"'{invalidUpstream}' is not a valid upstream. Use name=url, for example github=https://github.com.")
					.ConfigureAwait(false);
			}

			if (!TryParseAllows(
				parseResult.GetValue(allow),
				out Dictionary<string, List<string>> allowLists,
				out string? invalidAllow))
			{
				return await FailAsync(
					$"'{invalidAllow}' is not a valid allow entry. Use name=pattern, for example github=studio/**.")
					.ConfigureAwait(false);
			}

			return await RunAsync(overrides, allowLists, listenPort, configPath, cancellationToken)
				.ConfigureAwait(false);
		});

		return await root.Parse(args)
			.InvokeAsync(configuration: null, cancellationToken: CancellationToken.None)
			.ConfigureAwait(false);
	}

	private static async Task<int> RunAsync(
		Dictionary<string, string?> overrides,
		Dictionary<string, List<string>> allowLists,
		int port,
		string? configPath,
		CancellationToken cancellationToken)
	{
		WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			// The command line is parsed by System.CommandLine, not by the configuration provider, so
			// the friendly flags above are not also read as configuration keys.
			Args = [],
			ApplicationName = "ktsu.GitLfsCache",
		});

		// Added after the builder's own sources so an explicitly named file beats an appsettings.json
		// that happens to be in the working directory, and before ApplyDefaults, which reads
		// configuration to decide what still needs a default and would otherwise not see this file.
		if (configPath is not null)
		{
			builder.Configuration.AddJsonFile(configPath, optional: false, reloadOnChange: false);
		}

		ApplyDefaults(builder.Configuration, overrides);
		builder.Configuration.AddInMemoryCollection(overrides);
		builder.Configuration["Kestrel:Endpoints:Http:Url"] = $"http://*:{port}";

		builder.Services.AddGitLfsCache(builder.Configuration);
		builder.Services.PostConfigure<GitLfsCacheOptions>(options => ReplaceAllowLists(options, allowLists));

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
	/// <summary>
	/// Writes one line to standard error and reports the exit code to return.
	/// </summary>
	/// <remarks>
	/// A helper only so the argument checks above read as a list of conditions rather than as five
	/// copies of the same three lines.
	/// </remarks>
	/// <param name="message">What was wrong with the arguments.</param>
	/// <returns>The failing exit code.</returns>
	private static async Task<int> FailAsync(string message)
	{
		await Console.Error.WriteLineAsync(message).ConfigureAwait(false);
		return 1;
	}

	/// <summary>
	/// Splits a repeatable <c>name=value</c> flag.
	/// </summary>
	/// <remarks>
	/// A separator at either end is refused rather than producing an empty name or an empty value,
	/// both of which would bind configuration that cannot work and fail later with a worse message.
	/// </remarks>
	/// <param name="entry">The flag value as typed.</param>
	/// <param name="name">The part before the separator.</param>
	/// <param name="value">The part after it.</param>
	/// <returns><see langword="true"/> when the entry had both halves.</returns>
	private static bool TrySplitPair(string entry, out string name, out string value)
	{
		name = string.Empty;
		value = string.Empty;

		int separator = entry.IndexOf('=', StringComparison.Ordinal);

		if (separator <= 0 || separator == entry.Length - 1)
		{
			return false;
		}

		name = entry[..separator];
		value = entry[(separator + 1)..];
		return true;
	}

	/// <summary>
	/// Binds every <c>--upstream</c> flag to configuration.
	/// </summary>
	/// <param name="entries">The flag values, or null when the flag was not given.</param>
	/// <param name="overrides">Configuration to add to.</param>
	/// <param name="invalid">The first entry that could not be read, when one could not.</param>
	/// <returns><see langword="true"/> when every entry was well formed.</returns>
	private static bool TryApplyUpstreams(
		string[]? entries,
		Dictionary<string, string?> overrides,
		out string? invalid)
	{
		invalid = null;

		foreach (string entry in entries ?? [])
		{
			if (!TrySplitPair(entry, out string name, out string url))
			{
				invalid = entry;
				return false;
			}

			overrides[$"GitLfsCache:Upstreams:{name}:BaseUrl"] = url;
		}

		return true;
	}

	/// <summary>
	/// Resolves the <c>--store</c> flag against the working directory.
	/// </summary>
	/// <remarks>
	/// The options validator requires a fully qualified root, which keeps configuration files and
	/// environment variables strict for container deployments. A relative directory is what someone
	/// running the tool locally types, though, so the flag resolves it the way <c>--config</c> does
	/// rather than letting startup fail with a validation stack trace.
	/// </remarks>
	/// <param name="storeRoot">The directory as given on the command line.</param>
	/// <returns>The fully qualified directory.</returns>
	internal static string ResolveStoreRoot(string storeRoot) => Path.GetFullPath(storeRoot);

	/// <summary>
	/// Groups every <c>--allow</c> flag by the upstream it names.
	/// </summary>
	/// <param name="entries">The flag values, or null when the flag was not given.</param>
	/// <param name="allowLists">The patterns given for each upstream, in the order they were typed.</param>
	/// <param name="invalid">The first entry that could not be read, when one could not.</param>
	/// <returns><see langword="true"/> when every entry was well formed.</returns>
	internal static bool TryParseAllows(
		string[]? entries,
		out Dictionary<string, List<string>> allowLists,
		out string? invalid)
	{
		invalid = null;
		allowLists = new(StringComparer.OrdinalIgnoreCase);

		foreach (string entry in entries ?? [])
		{
			if (!TrySplitPair(entry, out string name, out string pattern))
			{
				invalid = entry;
				return false;
			}

			if (!allowLists.TryGetValue(name, out List<string>? patterns))
			{
				patterns = [];
				allowLists[name] = patterns;
			}

			patterns.Add(pattern);
		}

		return true;
	}

	/// <summary>
	/// Makes each upstream named by <c>--allow</c> allow exactly the patterns given for it.
	/// </summary>
	/// <remarks>
	/// Not written as configuration keys like the other flags. Configuration merges arrays by index,
	/// so <c>Repositories:0</c> from the command line would replace only the first entry of a list from
	/// a file or the environment and leave the rest in force: a file allowing <c>studio/**</c> and
	/// <c>**</c> plus <c>--allow github=only/**</c> would still allow everything. Assigning the list
	/// after binding, as a post-configure step, is what makes the flag narrow the list rather than patch
	/// it. Upstreams no flag names keep the list configuration gave them.
	/// </remarks>
	/// <param name="options">The options as bound from configuration.</param>
	/// <param name="allowLists">The patterns given for each upstream.</param>
	internal static void ReplaceAllowLists(
		GitLfsCacheOptions options,
		IReadOnlyDictionary<string, List<string>> allowLists)
	{
		foreach ((string name, List<string> patterns) in allowLists)
		{
			if (!options.Upstreams.TryGetValue(name, out UpstreamOptions? upstream))
			{
				upstream = new UpstreamOptions();
				options.Upstreams[name] = upstream;
			}

			upstream.Repositories.Clear();

			foreach (string pattern in patterns)
			{
				upstream.Repositories.Add(pattern);
			}
		}
	}

	private static void ApplyDefaults(ConfigurationManager configuration, Dictionary<string, string?> overrides)
	{
		bool hasStore = overrides.ContainsKey("GitLfsCache:Store:Root")
			|| !string.IsNullOrWhiteSpace(configuration[StoreRootKey]);

		if (!hasStore)
		{
			overrides[StoreRootKey] =
				UserDirectories.GetApplicationDataDirectory("ktsu", "GitLfsCache");
		}

		bool hasKey = overrides.ContainsKey("GitLfsCache:TokenKeys:0")
			|| !string.IsNullOrWhiteSpace(configuration[TokenKeyKey]);

		if (!hasKey)
		{
			overrides[TokenKeyKey] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

			Console.Error.WriteLine(
				"warning: no --token-key given, so a key was generated for this run. Transfer URLs already "
				+ "handed out will stop working when this process restarts, and a second instance cannot "
				+ "serve them. Set a key explicitly for anything beyond a single local process.");
		}
	}
}
