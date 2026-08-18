// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Configuration;

/// <summary>
/// Settings for the local object store.
/// </summary>
public sealed class StoreOptions
{
	/// <summary>
	/// Gets or sets the absolute directory holding the object trees and staging areas.
	/// </summary>
	public string Root { get; set; } = string.Empty;

	/// <summary>
	/// Gets or sets the byte budget, written with an optional suffix such as <c>500GB</c> or
	/// <c>500Gi</c>.
	/// </summary>
	public string MaxSize { get; set; } = "50GB";

	/// <summary>
	/// Gets or sets the fraction of the budget an eviction sweep reduces the store to, so sweeps do
	/// not thrash at the boundary. Must be greater than zero and less than one.
	/// </summary>
	public double LowWaterMark { get; set; } = 0.9;

	/// <summary>
	/// Gets or sets how long an orphaned staging file survives before cleanup removes it.
	/// </summary>
	public TimeSpan StagingMaxAge { get; set; } = TimeSpan.FromHours(6);

	/// <summary>
	/// Gets or sets how often the maintenance sweep runs.
	/// </summary>
	public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromMinutes(5);

	/// <summary>
	/// Gets the byte budget parsed from <see cref="MaxSize"/>.
	/// </summary>
	/// <exception cref="FormatException"><see cref="MaxSize"/> is not a valid byte size.</exception>
	public long MaxSizeBytes => SizeParser.Parse(MaxSize);
}
