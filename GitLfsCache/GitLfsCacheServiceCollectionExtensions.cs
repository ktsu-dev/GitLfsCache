// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache;

using System.IO.Abstractions;
using ktsu.Essentials;
using ktsu.Essentials.EncryptionProviders.Aes;
using ktsu.Essentials.FileSystemProviders.Native;
using ktsu.GitLfsCache.Batch;
using ktsu.GitLfsCache.Configuration;
using ktsu.GitLfsCache.Endpoints;
using ktsu.GitLfsCache.Fetching;
using ktsu.GitLfsCache.Locks;
using ktsu.GitLfsCache.Observability;
using ktsu.GitLfsCache.Storage;
using ktsu.GitLfsCache.Tokens;
using ktsu.GitLfsCache.Upstreams;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

/// <summary>
/// Registers the caching Git LFS proxy with a service collection.
/// </summary>
public static class GitLfsCacheServiceCollectionExtensions
{
	/// <summary>
	/// Adds every service the proxy needs.
	/// </summary>
	/// <param name="services">The service collection.</param>
	/// <param name="configuration">Configuration to bind the options from.</param>
	/// <returns>The same service collection, for chaining.</returns>
	public static IServiceCollection AddGitLfsCache(
		this IServiceCollection services,
		IConfiguration configuration)
	{
		Ensure.NotNull(services);
		Ensure.NotNull(configuration);

		services.AddOptions<GitLfsCacheOptions>()
			.Bind(configuration.GetSection(GitLfsCacheOptions.SectionName))
			.ValidateOnStart();

		services.AddSingleton<IValidateOptions<GitLfsCacheOptions>, GitLfsCacheOptionsValidator>();

		services.TryAddTimeProvider();

		// The store depends on IFileSystem rather than the Essentials marker interface, so a mock
		// filesystem can stand in during tests. The Essentials native provider supplies it at runtime.
		services.AddSingleton<IFileSystemProvider, NativeFileSystemProvider>();
		services.AddSingleton<IFileSystem>(provider => provider.GetRequiredService<IFileSystemProvider>());

		services.AddSingleton<IEncryptionProvider, AesEncryptionProvider>();

		services.AddSingleton<IUpstreamRegistry, UpstreamRegistry>();
		services.AddSingleton<IRepositoryAllowList, RepositoryAllowList>();
		services.AddSingleton<IHrefTokenCodec, HrefTokenCodec>();
		services.AddSingleton<BatchRewriter>();
		services.AddSingleton<IObjectStore, ObjectStore>();
		services.AddSingleton<IEvictionPolicy, LeastRecentlyUsedEvictionPolicy>();
		services.AddSingleton<IFetchCoalescer, FetchCoalescer>();

		// Its own instance, not the one inside FetchCoalescer, so an object key and a lock key cannot
		// collide however either is spelled.
		services.AddSingleton<ISingleFlight, SingleFlight>();
		services.AddSingleton<ICredentialAdmission, CredentialAdmission>();
		services.AddSingleton<ILockSnapshotStore, LockSnapshotStore>();
		services.AddSingleton<ILockListRefresher, LockListRefresher>();
		services.AddSingleton<LockListService>();
		services.AddSingleton<PublicUrlResolver>();
		services.AddSingleton<StoreReadiness>();
		services.AddSingleton<CacheMetrics>();
		services.AddSingleton<GitLfsCacheHandler>();

		services.AddMetrics();

		services.AddHostedService<StoreStartupCheck>();
		services.AddHostedService<StoreMaintenanceService>();

		// No timeout on the client: an object transfer legitimately takes as long as the object is
		// large, and the request's own cancellation already covers a client that gives up.
		services.AddHttpClient<IUpstreamClient, UpstreamClient>(UpstreamClient.HttpClientName, client =>
			client.Timeout = Timeout.InfiniteTimeSpan);

		return services;
	}

	private static void TryAddTimeProvider(this IServiceCollection services)
	{
		if (!services.Any(descriptor => descriptor.ServiceType == typeof(TimeProvider)))
		{
			services.AddSingleton(TimeProvider.System);
		}
	}

	private static void AddHostedService<THostedService>(this IServiceCollection services)
		where THostedService : class, Microsoft.Extensions.Hosting.IHostedService =>
		services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService, THostedService>();
}
