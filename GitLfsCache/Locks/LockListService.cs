// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Locks;

using ktsu.GitLfsCache.Configuration;
using ktsu.GitLfsCache.Fetching;
using ktsu.GitLfsCache.Observability;
using Microsoft.Extensions.Options;

/// <summary>
/// Answers a lock listing from a snapshot, refreshing it when it is stale.
/// </summary>
/// <remarks>
/// Everything that makes lock caching safe rather than merely fast lives here, in one place, because
/// the ordering between the parts is the safety property:
/// <list type="number">
/// <item>A snapshot is only served to a caller upstream recently accepted.</item>
/// <item>Admission is only ever granted by an upstream call that actually succeeded.</item>
/// <item>A stale snapshot is refreshed by exactly one caller, with that caller's own credential.</item>
/// </list>
/// Reordering any of those turns the cache into an authority, which is the thing this proxy exists
/// not to be.
/// </remarks>
/// <param name="snapshots">Holds the current snapshot per repository.</param>
/// <param name="refresher">Walks the listing upstream.</param>
/// <param name="admission">Remembers which credentials upstream accepted.</param>
/// <param name="flights">Keeps concurrent refreshes of one repository to a single walk.</param>
/// <param name="metrics">Cache counters.</param>
/// <param name="options">The configured options.</param>
/// <param name="timeProvider">Clock, injected so staleness is testable.</param>
public sealed class LockListService(
	ILockSnapshotStore snapshots,
	ILockListRefresher refresher,
	ICredentialAdmission admission,
	ISingleFlight flights,
	CacheMetrics metrics,
	IOptions<GitLfsCacheOptions> options,
	TimeProvider timeProvider)
{
	/// <summary>
	/// Obtains a snapshot to serve, refreshing first when necessary.
	/// </summary>
	/// <param name="key">The repository being listed.</param>
	/// <param name="upstreamBase">The configured upstream base URL.</param>
	/// <param name="authorization">The requesting client's Authorization header.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>What the handler should do with this request.</returns>
	public async Task<LockListOutcome> ResolveAsync(
		LockSnapshotKey key,
		Uri upstreamBase,
		string? authorization,
		CancellationToken cancellationToken)
	{
		Ensure.NotNull(key);

		LocksOptions locks = options.Value.Locks;

		if (!locks.Enabled)
		{
			return LockListOutcome.Relay();
		}

		LockSnapshot? current = snapshots.Read(key);
		bool usable = current is not null && !current.IsStale(timeProvider.GetUtcNow(), locks.ListTtl);

		// A caller already admitted, with a snapshot still inside its lifetime, is the steady state and
		// costs upstream nothing at all. This is the whole point of the subsystem.
		if (usable && admission.IsAdmitted(key.Upstream, key.RepositoryPath, authorization))
		{
			metrics.RecordLockListHit(key.Upstream);
			return LockListOutcome.Serve(current!);
		}

		// Everything below reaches upstream, either to refresh a stale snapshot or to prove a caller
		// may read one that is already fresh. Both admit on success, so a caller never pays twice.
		return usable
			? await ProbeAsync(key, upstreamBase, authorization, current!, cancellationToken).ConfigureAwait(false)
			: await RefreshAsync(key, upstreamBase, authorization, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Proves an unadmitted caller may read a snapshot that is already fresh.
	/// </summary>
	/// <remarks>
	/// A full walk would also prove it, but a repository with thousands of locks is many round trips
	/// and the snapshot in hand is already good. One page is the cheapest question that has the same
	/// answer.
	/// </remarks>
	private async Task<LockListOutcome> ProbeAsync(
		LockSnapshotKey key,
		Uri upstreamBase,
		string? authorization,
		LockSnapshot current,
		CancellationToken cancellationToken)
	{
		metrics.RecordLockAdmissionProbe(key.Upstream);

		LockRefreshResult probe = await refresher
			.ProbeAsync(key, upstreamBase, authorization, cancellationToken)
			.ConfigureAwait(false);

		if (probe.Outcome == LockRefreshOutcome.Refused)
		{
			metrics.RecordLockAdmissionRejected(key.Upstream);
			return LockListOutcome.Refuse(probe.Status!.Value);
		}

		if (probe.Outcome != LockRefreshOutcome.Succeeded)
		{
			return LockListOutcome.Relay();
		}

		admission.Admit(key.Upstream, key.RepositoryPath, authorization);
		return LockListOutcome.Serve(current);
	}

	private async Task<LockListOutcome> RefreshAsync(
		LockSnapshotKey key,
		Uri upstreamBase,
		string? authorization,
		CancellationToken cancellationToken)
	{
		using IFetchTicket ticket = flights.Acquire(key.ToFlightKey());

		if (!ticket.IsLeader)
		{
			metrics.RecordLockRefreshWait(key.Upstream);

			bool published = await ticket
				.WaitForLeaderAsync(options.Value.Locks.RefreshTimeout, cancellationToken)
				.ConfigureAwait(false);

			// A follower behind a leader that succeeded still has to prove its own credential: the
			// leader's authorization says nothing about this caller. It reruns the whole resolve, which
			// now finds a fresh snapshot and takes the cheap probe path.
			if (published)
			{
				return await ResolveAsync(key, upstreamBase, authorization, cancellationToken)
					.ConfigureAwait(false);
			}

			// The leader failed or stalled. Relaying is always correct and never waits again.
			return LockListOutcome.Relay();
		}

		metrics.RecordLockRefresh(key.Upstream);

		LockRefreshResult result = await refresher
			.RefreshAsync(key, upstreamBase, authorization, cancellationToken)
			.ConfigureAwait(false);

		switch (result.Outcome)
		{
			case LockRefreshOutcome.Succeeded:
				snapshots.Publish(key, result.Snapshot!);

				// The walk succeeding is itself upstream's answer that this caller may read these locks.
				admission.Admit(key.Upstream, key.RepositoryPath, authorization);
				ticket.Complete(published: true);
				return LockListOutcome.Serve(result.Snapshot!);

			case LockRefreshOutcome.Refused:
				metrics.RecordLockRefreshFailure(key.Upstream);
				ticket.Complete(published: false);
				return LockListOutcome.Refuse(result.Status!.Value);

			default:
				metrics.RecordLockRefreshFailure(key.Upstream);
				ticket.Complete(published: false);
				return LockListOutcome.Relay();
		}
	}
}
