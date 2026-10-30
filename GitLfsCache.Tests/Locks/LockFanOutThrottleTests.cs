// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Locks;

using System.Net;
using ktsu.GitLfsCache.Locks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class LockFanOutThrottleTests
{
	private static readonly DateTimeOffset Now = new(2026, 10, 21, 7, 28, 0, TimeSpan.Zero);

	private static HttpResponseMessage Response(HttpStatusCode status, string? retryAfter)
	{
		HttpResponseMessage response = new(status);

		if (retryAfter is not null)
		{
			response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
		}

		return response;
	}

	[TestMethod]
	[DataRow(HttpStatusCode.TooManyRequests)]
	[DataRow(HttpStatusCode.Forbidden)]
	public void TryGetThrottle_SecondsForm_IsTheDelay(HttpStatusCode status)
	{
		using HttpResponseMessage response = Response(status, "5");

		Assert.IsTrue(LockFanOut.TryGetThrottle(response, Now, out TimeSpan retryAfter));
		Assert.AreEqual(TimeSpan.FromSeconds(5), retryAfter);
	}

	[TestMethod]
	[DataRow(HttpStatusCode.TooManyRequests)]
	[DataRow(HttpStatusCode.Forbidden)]
	public void TryGetThrottle_HttpDateForm_IsTheTimeUntilThatDate(HttpStatusCode status)
	{
		using HttpResponseMessage response = Response(status, "Wed, 21 Oct 2026 07:28:30 GMT");

		Assert.IsTrue(LockFanOut.TryGetThrottle(response, Now, out TimeSpan retryAfter));
		Assert.AreEqual(TimeSpan.FromSeconds(30), retryAfter);
	}

	[TestMethod]
	public void TryGetThrottle_HttpDateInThePast_IsAThrottleWithNoDelay()
	{
		using HttpResponseMessage response = Response(HttpStatusCode.TooManyRequests, "Wed, 21 Oct 2026 07:27:00 GMT");

		Assert.IsTrue(LockFanOut.TryGetThrottle(response, Now, out TimeSpan retryAfter));
		Assert.AreEqual(TimeSpan.Zero, retryAfter);
	}

	[TestMethod]
	public void TryGetThrottle_NoRetryAfter_IsNotAThrottle()
	{
		using HttpResponseMessage response = Response(HttpStatusCode.Forbidden, null);

		Assert.IsFalse(LockFanOut.TryGetThrottle(response, Now, out _));
	}

	[TestMethod]
	public void TryGetThrottle_OtherStatusWithADate_IsNotAThrottle()
	{
		using HttpResponseMessage response = Response(HttpStatusCode.ServiceUnavailable, "Wed, 21 Oct 2026 07:28:30 GMT");

		Assert.IsFalse(LockFanOut.TryGetThrottle(response, Now, out _));
	}
}
