// Copyright (c) 2023-2026 ktsu.dev contributors

namespace ktsu.GitLfsCache.Upstreams;

/// <summary>
/// Resolves an upstream key taken from the request path to its configured base URL.
/// </summary>
public interface IUpstreamRegistry
{
	/// <summary>
	/// Resolves an upstream key.
	/// </summary>
	/// <param name="key">The first path segment of the request.</param>
	/// <param name="baseUrl">The configured base URL, or null when the key is unknown.</param>
	/// <returns><see langword="true"/> when the key is configured.</returns>
	public bool TryResolve(string key, out Uri? baseUrl);
}
