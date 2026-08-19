// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Fetching;

/// <summary>
/// Single-flight coordination keyed by upstream and object id.
/// </summary>
/// <remarks>
/// The first request for a missing object becomes the leader and fetches it. Later requests for the
/// same object wait for the leader and then read from the store. Followers therefore pay the leader's
/// full download latency before their first byte, which is the accepted trade for fetching each object
/// from upstream once. Having followers tail the leader's staging file so they stream concurrently is
/// a recorded improvement, deliberately deferred.
/// <para>
/// The coordination itself lives in <see cref="SingleFlight"/>, because the lock listing needs the
/// same leader-and-followers behaviour over a different key. This type owns a private instance rather
/// than taking a shared one, so an object key and a lock key cannot collide however they are spelled.
/// </para>
/// </remarks>
public sealed class FetchCoalescer : IFetchCoalescer
{
	private readonly SingleFlight _flights = new();

	/// <inheritdoc />
	public IFetchTicket Acquire(string upstream, string oid) => _flights.Acquire($"{upstream}/{oid}");
}
