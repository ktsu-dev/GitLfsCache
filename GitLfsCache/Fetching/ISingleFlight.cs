// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Fetching;

/// <summary>
/// Ensures only one operation per key is in flight at a time.
/// </summary>
/// <remarks>
/// The coordination the object cache needs for concurrent misses is the same coordination the lock
/// listing needs for concurrent refreshes: one caller does the work, the rest wait and then read what
/// it produced. Only the key differs, so the mechanism is shared and the meaning of the key is left
/// to the caller.
/// <para>
/// Instances do not share keys with each other, so two subsystems using this hold their own and
/// cannot collide however they spell a key.
/// </para>
/// </remarks>
public interface ISingleFlight
{
	/// <summary>
	/// Joins or starts the operation for one key.
	/// </summary>
	/// <param name="key">What is being coalesced.</param>
	/// <returns>
	/// A ticket that is either the leader, which must do the work and report the outcome, or a
	/// follower, which waits for the leader.
	/// </returns>
	public IFetchTicket Acquire(string key);
}
