// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Locks;

using System.Globalization;

/// <summary>
/// Identifies the lock listing of one repository.
/// </summary>
/// <remarks>
/// <paramref name="Ref"/> is part of the identity because the locking API's specification states the
/// <c>ref</c> property exists for authentication, which means two callers presenting different refs
/// may legitimately receive different answers from upstream. Sharing one entry between them would
/// serve one caller the other's view.
/// </remarks>
/// <param name="Upstream">The upstream key.</param>
/// <param name="RepositoryPath">The path between the upstream key and <c>/locks</c>.</param>
/// <param name="Ref">The ref the caller presented, or null when it presented none.</param>
public sealed record LockSnapshotKey(string Upstream, string RepositoryPath, string? Ref)
{
	/// <summary>
	/// Renders this key for use as a single-flight key.
	/// </summary>
	/// <remarks>
	/// Newline separated because it cannot appear in an upstream key, a repository path, or a ref
	/// name, so no two different keys can render identically.
	/// </remarks>
	/// <returns>The key as one string.</returns>
	public string ToFlightKey() => string.Create(
		CultureInfo.InvariantCulture,
		$"{Upstream}\n{RepositoryPath}\n{Ref}");
}
