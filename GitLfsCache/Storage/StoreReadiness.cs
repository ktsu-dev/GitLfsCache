// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Storage;

/// <summary>
/// Whether the store passed its startup check, for the readiness probe to report.
/// </summary>
/// <remarks>
/// A flag set once at startup rather than a live probe on every readiness call. Kubernetes polls
/// readiness every few seconds, and writing a probe file to the cache volume that often buys nothing:
/// a volume that goes read-only mid-life shows up as failed transfers and a cold cache, which is the
/// degraded behavior the design already accepts.
/// </remarks>
public sealed class StoreReadiness
{
	/// <summary>Gets a value indicating whether the store is usable.</summary>
	public bool IsReady { get; private set; }

	/// <summary>Gets why the store is not usable, when it is not.</summary>
	public string? FailureReason { get; private set; }

	/// <summary>Records that the store passed its startup check.</summary>
	public void MarkReady()
	{
		IsReady = true;
		FailureReason = null;
	}

	/// <summary>Records that the store failed its startup check.</summary>
	/// <param name="reason">What went wrong, for the probe response and the log.</param>
	public void MarkNotReady(string reason)
	{
		IsReady = false;
		FailureReason = reason;
	}
}
