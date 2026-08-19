// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Locks;

/// <summary>
/// Bounds how hard the proxy may push one upstream, across every request in the process.
/// </summary>
/// <remarks>
/// Fanning out a lock batch is the proxy deliberately making many calls at once, which is exactly the
/// shape a forge's abuse detection is built to notice. GitHub's secondary rate limits and Azure
/// DevOps' throttling are the expected outcome of doing this without a limiter, not an edge case.
/// <para>
/// Process wide and keyed by upstream rather than per request, because two clients each batching five
/// hundred paths would otherwise collectively exceed what the forge tolerates while each stayed
/// within its own limit.
/// </para>
/// </remarks>
public interface IUpstreamLimiter
{
	/// <summary>
	/// Waits for a slot against an upstream, and for any throttle on it to expire.
	/// </summary>
	/// <param name="upstream">The upstream key.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>A handle that releases the slot when disposed.</returns>
	public Task<IDisposable> AcquireAsync(string upstream, CancellationToken cancellationToken);

	/// <summary>
	/// Pauses every caller of an upstream, because it asked to be left alone.
	/// </summary>
	/// <remarks>
	/// Applied to the whole upstream rather than to the one call that was refused. A forge that
	/// throttled one request is telling the proxy about its own state, not about that request, and
	/// continuing to push the other in-flight calls would earn a longer ban.
	/// </remarks>
	/// <param name="upstream">The upstream key.</param>
	/// <param name="duration">How long upstream asked to be left alone.</param>
	public void Throttle(string upstream, TimeSpan duration);
}
