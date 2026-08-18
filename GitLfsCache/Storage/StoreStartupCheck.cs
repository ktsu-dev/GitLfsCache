// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Storage;

using System.IO.Abstractions;
using ktsu.GitLfsCache.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Refuses to start when the store root is not writable, and seeds the byte counter.
/// </summary>
/// <remarks>
/// Failing here rather than on the first clone is deliberate. A cache proxy whose volume is missing or
/// read-only looks healthy from the outside and then fails every transfer, which is a worse outcome
/// than a pod that will not start and says why.
/// <para>
/// The byte counter is rebuilt by a full scan, so a restart picks up whatever the volume already
/// holds rather than believing the store is empty and overfilling it.
/// </para>
/// </remarks>
/// <param name="fileSystem">The filesystem holding the store.</param>
/// <param name="store">The store whose counter is seeded.</param>
/// <param name="readiness">The flag the readiness probe reports.</param>
/// <param name="options">The configured options.</param>
/// <param name="logger">Logger.</param>
public sealed class StoreStartupCheck(
	IFileSystem fileSystem,
	IObjectStore store,
	StoreReadiness readiness,
	IOptions<GitLfsCacheOptions> options,
	ILogger<StoreStartupCheck> logger) : IHostedService
{
	/// <inheritdoc />
	public async Task StartAsync(CancellationToken cancellationToken)
	{
		string root = options.Value.Store.Root;

		try
		{
			fileSystem.Directory.CreateDirectory(root);

			string probe = fileSystem.Path.Combine(root, $".writable-{Guid.NewGuid():N}");
			await fileSystem.File.WriteAllTextAsync(probe, string.Empty, cancellationToken)
				.ConfigureAwait(false);
			fileSystem.File.Delete(probe);
		}
		catch (Exception failure) when (failure is IOException or UnauthorizedAccessException
			or NotSupportedException or ArgumentException)
		{
			readiness.MarkNotReady($"The store root '{root}' is not writable: {failure.Message}");

			throw new InvalidOperationException(
				$"The configured store root '{root}' is not writable. Check the volume mount and its permissions.",
				failure);
		}

		int objectCount = store.RecomputeTotalBytes();
		readiness.MarkReady();

		StoreLog.ReportedStoreSize(logger, objectCount, store.TotalBytes, options.Value.Store.MaxSizeBytes);
	}

	/// <inheritdoc />
	public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
