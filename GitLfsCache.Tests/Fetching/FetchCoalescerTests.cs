// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Fetching;

using ktsu.GitLfsCache.Fetching;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class FetchCoalescerTests
{
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

	private const string Oid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

	[TestMethod]
	public void Acquire_FirstCaller_IsTheLeader()
	{
		FetchCoalescer coalescer = new();

		using IFetchTicket ticket = coalescer.Acquire("github", Oid);

		Assert.IsTrue(ticket.IsLeader);
	}

	[TestMethod]
	public void Acquire_SecondCallerForTheSameObject_IsAFollower()
	{
		FetchCoalescer coalescer = new();

		using IFetchTicket leader = coalescer.Acquire("github", Oid);
		using IFetchTicket follower = coalescer.Acquire("github", Oid);

		Assert.IsTrue(leader.IsLeader);
		Assert.IsFalse(follower.IsLeader);
	}

	[TestMethod]
	public void Acquire_DifferentObjects_BothLead()
	{
		FetchCoalescer coalescer = new();

		using IFetchTicket first = coalescer.Acquire("github", Oid);
		using IFetchTicket second = coalescer.Acquire("github", new string('b', 64));

		Assert.IsTrue(first.IsLeader);
		Assert.IsTrue(second.IsLeader);
	}

	[TestMethod]
	public void Acquire_SameObjectFromDifferentUpstreams_BothLead()
	{
		FetchCoalescer coalescer = new();

		using IFetchTicket first = coalescer.Acquire("github", Oid);
		using IFetchTicket second = coalescer.Acquire("ado", Oid);

		Assert.IsTrue(first.IsLeader);
		Assert.IsTrue(second.IsLeader);
	}

	[TestMethod]
	public async Task WaitForLeaderAsync_LeaderPublished_ReturnsTrue()
	{
		FetchCoalescer coalescer = new();
		using IFetchTicket leader = coalescer.Acquire("github", Oid);
		using IFetchTicket follower = coalescer.Acquire("github", Oid);

		Task<bool> waiting = follower.WaitForLeaderAsync(Timeout, CancellationToken.None);
		leader.Complete(published: true);

		Assert.IsTrue(await waiting);
	}

	[TestMethod]
	public async Task WaitForLeaderAsync_LeaderFailed_ReturnsFalseSoTheFollowerFetches()
	{
		FetchCoalescer coalescer = new();
		using IFetchTicket leader = coalescer.Acquire("github", Oid);
		using IFetchTicket follower = coalescer.Acquire("github", Oid);

		Task<bool> waiting = follower.WaitForLeaderAsync(Timeout, CancellationToken.None);
		leader.Complete(published: false);

		Assert.IsFalse(await waiting);
	}

	[TestMethod]
	public async Task WaitForLeaderAsync_LeaderAbandonedWithoutReporting_ReleasesFollowers()
	{
		FetchCoalescer coalescer = new();
		IFetchTicket leader = coalescer.Acquire("github", Oid);
		using IFetchTicket follower = coalescer.Acquire("github", Oid);

		Task<bool> waiting = follower.WaitForLeaderAsync(Timeout, CancellationToken.None);

		// Disposal without Complete stands in for the leader's request faulting partway through.
		leader.Dispose();

		Assert.IsFalse(await waiting, "An abandoned leader must not strand its followers.");
	}

	[TestMethod]
	public async Task WaitForLeaderAsync_LeaderStalls_TimesOutAsAFailure()
	{
		FetchCoalescer coalescer = new();
		using IFetchTicket leader = coalescer.Acquire("github", Oid);
		using IFetchTicket follower = coalescer.Acquire("github", Oid);

		bool published = await follower.WaitForLeaderAsync(
			TimeSpan.FromMilliseconds(50),
			CancellationToken.None);

		Assert.IsFalse(published);
	}

	[TestMethod]
	public async Task WaitForLeaderAsync_Cancelled_Throws()
	{
		FetchCoalescer coalescer = new();
		using IFetchTicket leader = coalescer.Acquire("github", Oid);
		using IFetchTicket follower = coalescer.Acquire("github", Oid);
		using CancellationTokenSource cancellation = new();
		await cancellation.CancelAsync();

		await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () =>
			await follower.WaitForLeaderAsync(Timeout, cancellation.Token));
	}

	[TestMethod]
	public void Acquire_AfterTheLeaderCompleted_LeadsAgain()
	{
		FetchCoalescer coalescer = new();

		using (IFetchTicket first = coalescer.Acquire("github", Oid))
		{
			first.Complete(published: true);
		}

		using IFetchTicket second = coalescer.Acquire("github", Oid);

		// A later miss for the same object must not wait on a fetch that already finished.
		Assert.IsTrue(second.IsLeader);
	}

	[TestMethod]
	public void Complete_CalledByAFollower_Throws()
	{
		FetchCoalescer coalescer = new();
		using IFetchTicket leader = coalescer.Acquire("github", Oid);
		using IFetchTicket follower = coalescer.Acquire("github", Oid);

		Assert.ThrowsExactly<InvalidOperationException>(() => follower.Complete(published: true));
	}

	[TestMethod]
	public async Task WaitForLeaderAsync_CalledByTheLeader_Throws()
	{
		FetchCoalescer coalescer = new();
		using IFetchTicket leader = coalescer.Acquire("github", Oid);

		await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
			await leader.WaitForLeaderAsync(Timeout, CancellationToken.None));
	}

	[TestMethod]
	public async Task Acquire_ManyConcurrentCallers_ElectExactlyOneLeader()
	{
		FetchCoalescer coalescer = new();
		const int callers = 64;
		List<IFetchTicket> tickets = [];

		await Parallel.ForAsync(0, callers, (_, _) =>
		{
			IFetchTicket ticket = coalescer.Acquire("github", Oid);

			lock (tickets)
			{
				tickets.Add(ticket);
			}

			return ValueTask.CompletedTask;
		});

		try
		{
			Assert.AreEqual(1, tickets.Count(ticket => ticket.IsLeader));
			Assert.AreEqual(callers - 1, tickets.Count(ticket => !ticket.IsLeader));
		}
		finally
		{
			foreach (IFetchTicket ticket in tickets)
			{
				ticket.Dispose();
			}
		}
	}
}
