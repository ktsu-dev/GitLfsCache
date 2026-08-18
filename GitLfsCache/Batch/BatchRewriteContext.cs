// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Batch;

/// <summary>
/// The request context a batch response is rewritten against.
/// </summary>
public sealed record BatchRewriteContext
{
	/// <summary>Gets the configured upstream key from the request path.</summary>
	public required string Upstream { get; init; }

	/// <summary>
	/// Gets the path between the upstream key and <c>/objects/batch</c>, for example
	/// <c>owner/repo.git/info/lfs</c>, with no leading or trailing slash.
	/// </summary>
	public required string RepositoryPath { get; init; }

	/// <summary>Gets the externally reachable base URL rewritten hrefs are built from.</summary>
	public required Uri PublicBaseUrl { get; init; }
}
