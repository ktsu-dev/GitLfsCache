// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Locks;

using System.Collections.Concurrent;
using ktsu.GitLfsCache.Configuration;
using Microsoft.Extensions.Options;

/// <summary>
/// A semaphore and a throttle deadline per upstream, shared by every request in the process.
/// </summary>
/// <param name="options">The configured options.</param>
/// <param name="timeProvider">Clock, injected so throttle windows are testable.</param>
public sealed class UpstreamLimiter(
	IOptions<GitLfsCacheOptions> options,
	TimeProvider timeProvider) : IUpstreamLimiter, IDisposable
{
	private readonly ConcurrentDictionary<string, SemaphoreSlim> _slots =
		new(StringComparer.OrdinalIgnoreCase);

	private readonly ConcurrentDictionary<string, DateTimeOffset> _throttledUntil =
		new(StringComparer.OrdinalIgnoreCase);

	/// <inheritdoc />
	public async Task<IDisposable> AcquireAsync(string upstream, CancellationToken cancellationToken)
	{
		SemaphoreSlim slot = _slots.GetOrAdd(
			upstream,
			_ => new SemaphoreSlim(options.Value.Locks.MaxFanOutConcurrency));

		await slot.WaitAsync(cancellationToken).ConfigureAwait(false);

		try
		{
			// Waited out while holding the slot rather than before taking it, so a throttle does not
			// release a crowd of callers to hit upstream at the same instant the window closes.
			await WaitOutThrottleAsync(upstream, cancellationToken).ConfigureAwait(false);
		}
		catch
		{
			slot.Release();
			throw;
		}

		return new Slot(slot);
	}

	/// <inheritdoc />
	public void Throttle(string upstream, TimeSpan duration)
	{
		if (duration <= TimeSpan.Zero)
		{
			return;
		}

		DateTimeOffset until = timeProvider.GetUtcNow() + duration;

		// Extended, never shortened. A second refusal arriving with a shorter window must not undo a
		// longer one already in force.
		_throttledUntil.AddOrUpdate(
			upstream,
			until,
			(_, existing) => existing > until ? existing : until);
	}

	/// <inheritdoc />
	public void Dispose()
	{
		foreach (SemaphoreSlim slot in _slots.Values)
		{
			slot.Dispose();
		}
	}

	private async Task WaitOutThrottleAsync(string upstream, CancellationToken cancellationToken)
	{
		while (_throttledUntil.TryGetValue(upstream, out DateTimeOffset until))
		{
			TimeSpan remaining = until - timeProvider.GetUtcNow();

			if (remaining <= TimeSpan.Zero)
			{
				_throttledUntil.TryRemove(new KeyValuePair<string, DateTimeOffset>(upstream, until));
				return;
			}

			await Task.Delay(remaining, timeProvider, cancellationToken).ConfigureAwait(false);
		}
	}

	private sealed class Slot(SemaphoreSlim slot) : IDisposable
	{
		private bool _released;

		public void Dispose()
		{
			if (_released)
			{
				return;
			}

			_released = true;
			slot.Release();
		}
	}
}
