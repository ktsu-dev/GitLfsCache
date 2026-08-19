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
/// <param name="LockId">
/// The lock id, for a <see cref="LfsRouteKind.LocksUnlock"/> route. Null otherwise.
/// </param>
/// <remarks>
/// <paramref name="LockId"/> is separate from <paramref name="Oid"/> rather than reusing it, because
/// an object id is a SHA256 digest the proxy validates and addresses storage by, while a lock id is
/// an opaque string the forge assigns and the proxy only ever passes back.
/// </remarks>
public sealed record LfsRoute(
	LfsRouteKind Kind,
	string Upstream,
	string RepositoryPath,
	string? Oid,
	string RelayPath,
	string? LockId = null);
