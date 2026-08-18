// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Storage;

using ktsu.GitLfsCache.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Runs the eviction sweep and staging cleanup on an interval.
/// </summary>
/// <remarks>
/// Both jobs are periodic rather than triggered per request, so a burst of misses does not turn every
/// transfer into a filesystem scan. The interval is what bounds how far over budget the store can go
/// between sweeps, which is why the volume should be provisioned with some headroom above the budget.
/// </remarks>
/// <param name="evictionPolicy">The policy that frees space.</param>
/// <param name="store">The store whose staging files are cleaned.</param>
/// <param name="options">The configured options.</param>
/// <param name="timeProvider">Clock, injected so the sweep interval is testable.</param>
/// <param name="logger">Logger.</param>
public sealed class StoreMaintenanceService(
	IEvictionPolicy evictionPolicy,
	IObjectStore store,
	IOptions<GitLfsCacheOptions> options,
	TimeProvider timeProvider,
	ILogger<StoreMaintenanceService> logger) : BackgroundService
{
	/// <inheritdoc />
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		using PeriodicTimer timer = new(options.Value.Store.MaintenanceInterval, timeProvider);

		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
				RunSweep();
			}
			catch (OperationCanceledException)
			{
				// Shutdown, not a failure.
				return;
			}
			catch (Exception failure) when (failure is IOException or UnauthorizedAccessException
				or FormatException)
			{
				// A sweep that fails must not take the host down with it. The store keeps serving,
				// and the next interval tries again.
				StoreLog.MaintenanceSweepFailed(logger, failure);
			}
		}
	}

	private void RunSweep()
	{
		EvictionResult eviction = evictionPolicy.Evict();

		if (eviction.EvictedCount > 0 || eviction.SkippedCount > 0)
		{
			StoreLog.EvictedObjects(
				logger,
				eviction.EvictedCount,
				eviction.EvictedBytes,
				eviction.TargetBytes);
		}

		TimeSpan maxAge = options.Value.Store.StagingMaxAge;
		DateTimeOffset cutoff = timeProvider.GetUtcNow() - maxAge;
		int removed = 0;

		foreach (StagedFile staged in store.EnumerateStaging().ToList())
		{
			if (staged.CreatedUtc <= cutoff && store.TryDeleteStaging(staged))
			{
				removed++;
			}
		}

		if (removed > 0)
		{
			StoreLog.RemovedOrphanedStaging(logger, removed, maxAge);
		}
	}
}
