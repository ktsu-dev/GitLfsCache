// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Upstreams;

/// <summary>
/// Decides whether an upstream may be used for a given repository path.
/// </summary>
public interface IRepositoryAllowList
{
	/// <summary>
	/// Reports whether a path is allowed for an upstream.
	/// </summary>
	/// <param name="upstream">The resolved upstream key.</param>
	/// <param name="path">The whole request path following the upstream key.</param>
	/// <returns><see langword="true"/> when a configured pattern matches.</returns>
	public bool IsAllowed(string upstream, string path);
}
