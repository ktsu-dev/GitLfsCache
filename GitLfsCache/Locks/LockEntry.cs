// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Locks;

using System.Text.Json.Nodes;

/// <summary>
/// One lock as upstream described it.
/// </summary>
/// <remarks>
/// <paramref name="Payload"/> is upstream's object kept whole rather than a set of modelled fields.
/// The locking API is explicitly described by its specification as a first version designed to be
/// extended, and the proxy has no reason to be the thing that drops a field a forge added: it only
/// needs <paramref name="Id"/> and <paramref name="Path"/> to filter, and everything else travels
/// back to the client exactly as it arrived.
/// </remarks>
/// <param name="Id">The forge-assigned lock id.</param>
/// <param name="Path">The repository-relative path the lock is held on.</param>
/// <param name="Payload">Upstream's entire object for this lock.</param>
public sealed record LockEntry(string Id, string Path, JsonObject Payload);
