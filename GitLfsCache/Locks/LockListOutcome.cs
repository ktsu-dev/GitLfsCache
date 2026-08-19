// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Locks;

using System.Net;

/// <summary>
/// What the handler should do with one lock listing request.
/// </summary>
/// <remarks>
/// Relaying is the answer to every problem the cache has with itself: a body it could not understand,
/// a repository with too many locks, a leader that stalled, the subsystem being switched off. Each of
/// those is a reason to stop caching, never a reason to fail a client, because the proxy relayed all
/// of this perfectly well before the cache existed.
/// </remarks>
/// <param name="Kind">Which of the outcomes this is.</param>
/// <param name="Snapshot">The snapshot to serve from, when serving.</param>
/// <param name="Status">Upstream's status, when refusing.</param>
public sealed record LockListOutcome(LockListOutcomeKind Kind, LockSnapshot? Snapshot, HttpStatusCode? Status)
{
	/// <summary>Serve the listing from this snapshot.</summary>
	/// <param name="snapshot">The snapshot to serve.</param>
	/// <returns>The outcome.</returns>
	public static LockListOutcome Serve(LockSnapshot snapshot) =>
		new(LockListOutcomeKind.Serve, snapshot, null);

	/// <summary>Relay the request upstream, as if the cache were not there.</summary>
	/// <returns>The outcome.</returns>
	public static LockListOutcome Relay() => new(LockListOutcomeKind.Relay, null, null);

	/// <summary>Return upstream's refusal to the client.</summary>
	/// <param name="status">Upstream's status.</param>
	/// <returns>The outcome.</returns>
	public static LockListOutcome Refuse(HttpStatusCode status) =>
		new(LockListOutcomeKind.Refuse, null, status);
}

/// <summary>
/// The kinds of answer a lock listing request can get.
/// </summary>
public enum LockListOutcomeKind
{
	/// <summary>Answer from the snapshot.</summary>
	Serve,

	/// <summary>Relay upstream instead of caching.</summary>
	Relay,

	/// <summary>Return upstream's refusal.</summary>
	Refuse,
}
