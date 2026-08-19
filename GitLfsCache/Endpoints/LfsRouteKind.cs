// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Endpoints;

/// <summary>
/// What a request path under an upstream prefix addresses.
/// </summary>
public enum LfsRouteKind
{
	/// <summary>Anything the proxy does not terminate itself, relayed upstream verbatim.</summary>
	Relay,

	/// <summary>The Batch API, at <c>.../objects/batch</c>.</summary>
	Batch,

	/// <summary>An object transfer, at <c>.../objects/{oid}</c>.</summary>
	Transfer,

	/// <summary>An upload verification, at <c>.../objects/{oid}/verify</c>.</summary>
	Verify,

	/// <summary>
	/// The lock collection, at <c>.../locks</c>. Listing and creation share a path and are told
	/// apart by method, which the parser does not see.
	/// </summary>
	Locks,

	/// <summary>Push-time lock verification, at <c>.../locks/verify</c>.</summary>
	LocksVerify,

	/// <summary>Batched locking, at <c>.../locks/batch</c>. A proxy extension, not a specification endpoint.</summary>
	LocksBatch,

	/// <summary>Releasing one lock, at <c>.../locks/{id}/unlock</c>.</summary>
	LocksUnlock,
}
