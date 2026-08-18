// Copyright (c) 2023-2026 ktsu.dev contributors

namespace ktsu.GitLfsCache.Configuration;

/// <summary>
/// Settings governing upstream fetches on a cache miss.
/// </summary>
public sealed class FetchOptions
{
	/// <summary>
	/// Gets or sets how long a follower waits for the leader's fetch to publish before giving up
	/// and fetching from upstream itself.
	/// </summary>
	public TimeSpan FollowerTimeout { get; set; } = TimeSpan.FromMinutes(5);
}
