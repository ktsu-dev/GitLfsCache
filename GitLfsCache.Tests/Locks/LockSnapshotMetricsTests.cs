// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Locks;

using System.Globalization;
using System.Net;
using System.Text;
using ktsu.GitLfsCache.Configuration;
using ktsu.GitLfsCache.Locks;
using ktsu.GitLfsCache.Observability;
using ktsu.GitLfsCache.Tests.Observability;
using ktsu.GitLfsCache.Upstreams;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class LockSnapshotMetricsTests : IDisposable
{
	private const string Upstream = "github";
	private const string Repository = "owner/repo.git/info/lfs";

	private static readonly Uri UpstreamBase = new("https://github.com/owner/repo.git/info/lfs/");

	private readonly RecordingMeterFactory _meterFactory = new();
	private readonly CacheMetrics _metrics;
	private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 8, 19, 9, 47, 0, TimeSpan.Zero));

	public LockSnapshotMetricsTests() => _metrics = new CacheMetrics(_meterFactory);

	public void Dispose()
	{
		_metrics.Dispose();
		_meterFactory.Dispose();
	}

	private static IOptions<GitLfsCacheOptions> Options(int maxSnapshotLocks = 100_000) =>
		Microsoft.Extensions.Options.Options.Create(new GitLfsCacheOptions
		{
			Locks = new LocksOptions { MaxSnapshotLocks = maxSnapshotLocks },
		});

	private static LockSnapshotKey Key() => new(Upstream, Repository, "refs/heads/main");

	private static LockEntry[] Locks(int count)
	{
		StringBuilder body = new("""{"locks":[""");

		for (int index = 0; index < count; index++)
		{
			body.Append(index == 0 ? string.Empty : ",");
			body.Append(CultureInfo.InvariantCulture, $$"""{"id":"{{index}}","path":"Content/{{index}}.uasset"}""");
		}

		body.Append("]}");
		Assert.IsTrue(LockListParser.TryParsePage(
			System.Text.Json.Nodes.JsonNode.Parse(body.ToString()),
			out IReadOnlyList<LockEntry>? entries,
			out _));

		return [.. entries!];
	}

	[TestMethod]
	public void Publish_RecordsTheSnapshotSize()
	{
		LockSnapshotStore store = new(Options(), _time, _metrics);

		store.Publish(Key(), new LockSnapshot(Locks(3), _time.GetUtcNow()));

		Assert.AreEqual(3L, _meterFactory.Total("gitlfscache.lock_snapshot_size", Upstream));
	}

	[TestMethod]
	public void Publish_EmptySnapshot_RecordsZeroLocks()
	{
		LockSnapshotStore store = new(Options(), _time, _metrics);

		store.Publish(Key(), new LockSnapshot([], _time.GetUtcNow()));

		Assert.AreEqual(0L, _meterFactory.Total("gitlfscache.lock_snapshot_size", Upstream));
		Assert.AreEqual(1L, _meterFactory.Count("gitlfscache.lock_snapshot_size", Upstream));
	}

	[TestMethod]
	public async Task Refresh_OverTheLockCeiling_RecordsAnOversizeWalk()
	{
		using SinglePageUpstream upstream = new("""{"locks":[{"id":"1","path":"a"},{"id":"2","path":"b"},{"id":"3","path":"c"}]}""");
		LockListRefresher refresher = new(upstream, Options(maxSnapshotLocks: 2), _time, _metrics);

		LockRefreshResult result = await refresher.RefreshAsync(Key(), UpstreamBase, authorization: null, CancellationToken.None);

		Assert.AreEqual(LockRefreshOutcome.TooLarge, result.Outcome);
		Assert.AreEqual(1L, _meterFactory.Total("gitlfscache.lock_snapshot_oversize", Upstream));
	}

	[TestMethod]
	public async Task Refresh_UnderTheLockCeiling_RecordsNoOversizeWalk()
	{
		using SinglePageUpstream upstream = new("""{"locks":[{"id":"1","path":"a"}]}""");
		LockListRefresher refresher = new(upstream, Options(maxSnapshotLocks: 2), _time, _metrics);

		LockRefreshResult result = await refresher.RefreshAsync(Key(), UpstreamBase, authorization: null, CancellationToken.None);

		Assert.AreEqual(LockRefreshOutcome.Succeeded, result.Outcome);
		Assert.AreEqual(0L, _meterFactory.Total("gitlfscache.lock_snapshot_oversize", Upstream));
	}

	/// <summary>An upstream that answers every lock listing with the same single page.</summary>
	private sealed class SinglePageUpstream(string body) : IUpstreamClient, IDisposable
	{
		private readonly List<HttpResponseMessage> _responses = [];

		public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			request.Dispose();

			HttpResponseMessage response = new(HttpStatusCode.OK)
			{
				Content = new StringContent(body, Encoding.UTF8, "application/vnd.git-lfs+json"),
			};

			_responses.Add(response);
			return Task.FromResult(response);
		}

		public void Dispose()
		{
			foreach (HttpResponseMessage response in _responses)
			{
				response.Dispose();
			}
		}
	}
}
