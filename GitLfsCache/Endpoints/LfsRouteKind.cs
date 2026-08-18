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
}
