// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Locks;

using ktsu.GitLfsCache.Configuration;
using ktsu.GitLfsCache.Locks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class UpstreamLimiterTests
{
	private static (UpstreamLimiter Limiter, FakeTimeProvider Time) Create(TimeSpan maxThrottlePause)
	{
		GitLfsCacheOptions options = new();
		options.Locks.MaxThrottlePause = maxThrottlePause;
		FakeTimeProvider time = new(new DateTimeOffset(2026, 10, 18, 7, 0, 0, TimeSpan.Zero));
		return (new UpstreamLimiter(Options.Create(options), time), time);
	}

	[TestMethod]
	[DataRow(5000000, DisplayName = "past what Task.Delay can wait")]
	[DataRow(3600, DisplayName = "an hour")]
	public async Task Throttle_BeyondTheCeiling_PausesForTheCeilingAndNoLonger(int retryAfterSeconds)
	{
		(UpstreamLimiter limiter, FakeTimeProvider time) = Create(TimeSpan.FromSeconds(60));
		using (limiter)
		{
			limiter.Throttle("github", TimeSpan.FromSeconds(retryAfterSeconds));

			Task<IDisposable> acquire = limiter.AcquireAsync("github", CancellationToken.None);

			time.Advance(TimeSpan.FromSeconds(59));
			Assert.IsFalse(acquire.IsCompleted, "the pause must still hold just inside the ceiling");

			time.Advance(TimeSpan.FromSeconds(1));
			using IDisposable slot = await acquire.WaitAsync(TimeSpan.FromSeconds(10));
		}
	}

	[TestMethod]
	public async Task Throttle_WithinTheCeiling_PausesForTheWholeDuration()
	{
		(UpstreamLimiter limiter, FakeTimeProvider time) = Create(TimeSpan.FromSeconds(60));
		using (limiter)
		{
			limiter.Throttle("github", TimeSpan.FromSeconds(30));

			Task<IDisposable> acquire = limiter.AcquireAsync("github", CancellationToken.None);

			time.Advance(TimeSpan.FromSeconds(29));
			Assert.IsFalse(acquire.IsCompleted);

			time.Advance(TimeSpan.FromSeconds(1));
			using IDisposable slot = await acquire.WaitAsync(TimeSpan.FromSeconds(10));
		}
	}
}
