// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Storage;

using Microsoft.Extensions.Logging;

/// <summary>
/// Source-generated log messages for the object store.
/// </summary>
/// <remarks>
/// Declared separately from the store itself so the store keeps its primary constructor. The
/// generated delegates avoid boxing and skip argument evaluation when the level is disabled, which is
/// what CA1848 and CA1873 are asking for on a path that runs once per served object.
/// </remarks>
internal static partial class StoreLog
{
	[LoggerMessage(
		EventId = 1000,
		Level = LogLevel.Warning,
		Message = "Could not open cached object {Oid} for {Upstream}.")]
	public static partial void CouldNotOpenObject(ILogger logger, Exception exception, string oid, string upstream);

	[LoggerMessage(
		EventId = 1001,
		Level = LogLevel.Warning,
		Message = "Discarding fetched object for {Upstream}: content hashed to {Digest}, not {Oid}.")]
	public static partial void DiscardedMismatchedObject(
		ILogger logger,
		string upstream,
		string digest,
		string oid);

	[LoggerMessage(
		EventId = 1002,
		Level = LogLevel.Warning,
		Message = "Could not publish object {Oid} for {Upstream}.")]
	public static partial void CouldNotPublishObject(ILogger logger, Exception exception, string oid, string upstream);

	[LoggerMessage(
		EventId = 1003,
		Level = LogLevel.Debug,
		Message = "Could not update the access time for {Oid}.")]
	public static partial void CouldNotUpdateAccessTime(ILogger logger, Exception exception, string oid);

	[LoggerMessage(
		EventId = 1004,
		Level = LogLevel.Debug,
		Message = "Could not delete {Path}; leaving it for the next sweep.")]
	public static partial void CouldNotDeleteFile(ILogger logger, Exception exception, string path);

	[LoggerMessage(
		EventId = 1005,
		Level = LogLevel.Information,
		Message = "Store holds {ObjectCount} objects totalling {TotalBytes} bytes against a {BudgetBytes} byte budget.")]
	public static partial void ReportedStoreSize(
		ILogger logger,
		int objectCount,
		long totalBytes,
		long budgetBytes);

	[LoggerMessage(
		EventId = 1006,
		Level = LogLevel.Information,
		Message = "Evicted {EvictedCount} objects totalling {EvictedBytes} bytes to get under {TargetBytes} bytes.")]
	public static partial void EvictedObjects(
		ILogger logger,
		int evictedCount,
		long evictedBytes,
		long targetBytes);

	[LoggerMessage(
		EventId = 1007,
		Level = LogLevel.Information,
		Message = "Removed {StagingCount} staging files orphaned for longer than {MaxAge}.")]
	public static partial void RemovedOrphanedStaging(ILogger logger, int stagingCount, TimeSpan maxAge);

	[LoggerMessage(
		EventId = 1008,
		Level = LogLevel.Error,
		Message = "The store maintenance sweep failed and will be retried on the next interval.")]
	public static partial void MaintenanceSweepFailed(ILogger logger, Exception exception);
}
