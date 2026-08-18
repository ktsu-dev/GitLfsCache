// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Fetching;

using System.Collections.Concurrent;

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
/// A leader that is abandoned without reporting an outcome releases its followers as a failure when
/// its ticket is disposed, so a crash on the leader's path cannot strand anyone.
/// </para>
/// </remarks>
public sealed class FetchCoalescer : IFetchCoalescer
{
	private readonly ConcurrentDictionary<string, Entry> _inFlight = new(StringComparer.Ordinal);

	/// <inheritdoc />
	public IFetchTicket Acquire(string upstream, string oid)
	{
		string key = $"{upstream}/{oid}";

		while (true)
		{
			Entry entry = _inFlight.GetOrAdd(key, _ => new Entry());

			lock (entry.Gate)
			{
				// A leader that finished between GetOrAdd and this lock leaves a retired entry behind.
				// Retrying gets a fresh one rather than waiting on a fetch that is already over.
				if (entry.Retired)
				{
					continue;
				}

				if (!entry.HasLeader)
				{
					entry.HasLeader = true;
					return new Ticket(this, key, entry, isLeader: true);
				}

				return new Ticket(this, key, entry, isLeader: false);
			}
		}
	}

	private void Retire(string key, Entry entry)
	{
		lock (entry.Gate)
		{
			entry.Retired = true;
		}

		// Only remove the entry this ticket owns, so a newer fetch for the same object is untouched.
		_inFlight.TryRemove(new KeyValuePair<string, Entry>(key, entry));
	}

	private sealed class Entry
	{
		public Lock Gate { get; } = new();

		public TaskCompletionSource<bool> Completion { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public bool HasLeader { get; set; }

		public bool Retired { get; set; }
	}

	private sealed class Ticket(FetchCoalescer owner, string key, Entry entry, bool isLeader) : IFetchTicket
	{
		private bool _disposed;

		public bool IsLeader => isLeader;

		public async Task<bool> WaitForLeaderAsync(TimeSpan timeout, CancellationToken cancellationToken)
		{
			if (isLeader)
			{
				throw new InvalidOperationException("The leader does not wait for itself.");
			}

			try
			{
				return await entry.Completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
			}
			catch (TimeoutException)
			{
				// The leader is stalled. Reported as a failure so this follower fetches for itself.
				return false;
			}
		}

		public void Complete(bool published)
		{
			if (!isLeader)
			{
				throw new InvalidOperationException("Only the leader reports the outcome of a fetch.");
			}

			entry.Completion.TrySetResult(published);
			owner.Retire(key, entry);
		}

		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;

			if (!isLeader)
			{
				return;
			}

			// A leader that never reported an outcome releases its followers as a failure rather than
			// leaving them waiting for a fetch that is no longer happening.
			entry.Completion.TrySetResult(false);
			owner.Retire(key, entry);
		}
	}
}
