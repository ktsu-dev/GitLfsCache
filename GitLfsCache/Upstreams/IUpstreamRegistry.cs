// Copyright (c) 2023-2026 ktsu-dev contributors

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

	/// <summary>
	/// Resolves an upstream key and reports the spelling it is configured under.
	/// </summary>
	/// <remarks>
	/// Keys match regardless of case, so <c>/GitHub/</c> and <c>/github/</c> reach the same upstream.
	/// Everything keyed by the upstream afterwards (the object store, fetch coalescing, transfer tokens,
	/// lock snapshots and metrics) must use <paramref name="canonicalKey"/>, or each spelling gets its
	/// own cold cache. The default implementation returns <paramref name="key"/> unchanged.
	/// </remarks>
	/// <param name="key">The first path segment of the request.</param>
	/// <param name="canonicalKey">The key as configured, or <paramref name="key"/> when it is unknown.</param>
	/// <param name="baseUrl">The configured base URL, or null when the key is unknown.</param>
	/// <returns><see langword="true"/> when the key is configured.</returns>
	public bool TryResolve(string key, out string canonicalKey, out Uri? baseUrl)
	{
		canonicalKey = key;
		return TryResolve(key, out baseUrl);
	}
}
