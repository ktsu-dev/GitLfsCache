// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Fetching;

/// <summary>
/// One request's place in the fetch for a single object.
/// </summary>
public interface IFetchTicket : IDisposable
{
	/// <summary>
	/// Gets a value indicating whether this request must perform the fetch itself.
	/// </summary>
	public bool IsLeader { get; }

	/// <summary>
	/// Waits for the leader's fetch to finish.
	/// </summary>
	/// <param name="timeout">
	/// How long to wait before giving up on the leader. A follower that waits forever behind a stalled
	/// leader is worse than one that fetches for itself.
	/// </param>
	/// <param name="cancellationToken">Cancellation token, typically the client's disconnect.</param>
	/// <returns>
	/// <see langword="true"/> when the leader published the object, so it can be served from the
	/// store. <see langword="false"/> when the leader failed or did not finish in time, in which case
	/// the caller should fetch upstream itself.
	/// </returns>
	public Task<bool> WaitForLeaderAsync(TimeSpan timeout, CancellationToken cancellationToken);

	/// <summary>
	/// Reports the outcome of the leader's fetch, releasing every waiting follower.
	/// </summary>
	/// <param name="published">Whether the object was verified and published to the store.</param>
	public void Complete(bool published);
}
