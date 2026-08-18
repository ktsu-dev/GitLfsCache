// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Storage;

using ktsu.Semantics.Paths;

/// <summary>
/// One published object in the store.
/// </summary>
/// <param name="Path">The absolute path of the object file.</param>
/// <param name="Upstream">The upstream key whose tree holds it.</param>
/// <param name="Oid">The object id, a lowercase hex SHA256 digest.</param>
/// <param name="Size">The object size in bytes.</param>
/// <param name="LastAccessUtc">When the object was last served, used to order eviction.</param>
public sealed record StoredObject(
	AbsoluteFilePath Path,
	string Upstream,
	string Oid,
	long Size,
	DateTimeOffset LastAccessUtc);
