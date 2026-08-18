// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Storage;

using ktsu.Semantics.Paths;

/// <summary>
/// An in-progress or orphaned staging file.
/// </summary>
/// <param name="Path">The absolute path of the staging file.</param>
/// <param name="CreatedUtc">When the staging file was created, used to age out orphans.</param>
public sealed record StagedFile(AbsoluteFilePath Path, DateTimeOffset CreatedUtc);
