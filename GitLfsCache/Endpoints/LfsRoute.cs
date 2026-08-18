// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Endpoints;

/// <summary>
/// A parsed request path.
/// </summary>
/// <param name="Kind">What the path addresses.</param>
/// <param name="Upstream">The configured upstream key, taken from the first path segment.</param>
/// <param name="RepositoryPath">
/// The path between the upstream key and the Git LFS endpoint, for example
/// <c>owner/repo.git/info/lfs</c>. Empty for a relay.
/// </param>
/// <param name="Oid">The object id, for an object or verify route. Null otherwise.</param>
/// <param name="RelayPath">
/// The whole path after the upstream key, used verbatim when relaying.
/// </param>
public sealed record LfsRoute(
	LfsRouteKind Kind,
	string Upstream,
	string RepositoryPath,
	string? Oid,
	string RelayPath);
