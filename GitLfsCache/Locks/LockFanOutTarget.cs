// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Locks;

/// <summary>
/// One item of a batched lock request.
/// </summary>
/// <remarks>
/// A release may name either a path or an id. A path has to be resolved to an id against the current
/// snapshot before it can be released, which is one of the few places the listing cache does work for
/// a caller rather than merely answering them faster.
/// </remarks>
/// <param name="Path">The repository-relative path, or null when the client named an id.</param>
/// <param name="Id">The forge-assigned lock id, or null when the client named a path.</param>
public sealed record LockFanOutTarget(string? Path, string? Id);
