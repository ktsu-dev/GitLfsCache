// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Fetching;

/// <summary>
/// Ensures only one upstream fetch per object is in flight at a time.
/// </summary>
/// <remarks>
/// Without this, a fleet of pods cloning the same repository at once turns every cache miss into one
/// upstream transfer per pod, which is the cost the cache exists to remove.
/// </remarks>
public interface IFetchCoalescer
{
	/// <summary>
	/// Joins or starts the fetch for one object.
	/// </summary>
	/// <param name="upstream">The upstream key.</param>
	/// <param name="oid">The object id.</param>
	/// <returns>
	/// A ticket that is either the leader, which must perform the fetch and report the outcome, or a
	/// follower, which waits for the leader.
	/// </returns>
	public IFetchTicket Acquire(string upstream, string oid);
}
