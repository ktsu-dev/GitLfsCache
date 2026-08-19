// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Locks;

/// <summary>
/// What a batched lock request does.
/// </summary>
public enum LockFanOutOperation
{
	/// <summary>The body named no operation this proxy understands.</summary>
	Unknown,

	/// <summary>Take a lock on every target.</summary>
	Lock,

	/// <summary>Release the lock on every target.</summary>
	Unlock,
}
