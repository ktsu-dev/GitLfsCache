// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Locks;

/// <summary>
/// The kinds of outcome a listing walk can have.
/// </summary>
public enum LockRefreshOutcome
{
	/// <summary>Every page was read and a snapshot was assembled.</summary>
	Succeeded,

	/// <summary>Upstream refused, and its answer belongs to the client verbatim.</summary>
	Refused,

	/// <summary>A response could not be understood, so this repository should be relayed.</summary>
	Unusable,

	/// <summary>The repository holds more locks than may be cached, so it should be relayed.</summary>
	TooLarge,
}
