// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Storage;

/// <summary>
/// Decides which objects to delete when the store exceeds its byte budget.
/// </summary>
public interface IEvictionPolicy
{
	/// <summary>
	/// Brings the store back under its budget if it has exceeded it.
	/// </summary>
	/// <returns>What the sweep did.</returns>
	public EvictionResult Evict();
}
