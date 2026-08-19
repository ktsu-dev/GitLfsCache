// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Observability;

using System.Diagnostics.Metrics;

/// <summary>
/// Counters describing how well the cache is doing its job.
/// </summary>
/// <remarks>
/// Built on <see cref="Meter"/> with no exporter dependency. Adding an OpenTelemetry exporter is a few
/// lines in the host, and pinning one here would be a guess about the scraping setup.
/// <para>
/// The hit and miss counters are the pair worth watching: a proxy with a low hit ratio is costing
/// latency without saving bandwidth, which usually means the volume is too small for the working set.
/// </para>
/// </remarks>
public sealed class CacheMetrics : IDisposable
{
	/// <summary>The meter name to subscribe to when exporting these counters.</summary>
	public const string MeterName = "ktsu.GitLfsCache";

	/// <summary>The UCUM annotation for a count of objects.</summary>
	private const string ObjectUnit = "{object}";

	/// <summary>The UCUM annotation for a count of requests or calls.</summary>
	private const string RequestUnit = "{request}";

	private readonly Meter _meter;
	private readonly Counter<long> _hits;
	private readonly Counter<long> _misses;
	private readonly Counter<long> _bytesServedFromCache;
	private readonly Counter<long> _bytesFetchedUpstream;
	private readonly Counter<long> _bytesUploaded;
	private readonly Counter<long> _objectsStored;
	private readonly Counter<long> _verificationFailures;
	private readonly Counter<long> _coalescedWaits;
	private readonly Counter<long> _rejectedTokens;
	private readonly Counter<long> _lockListHits;
	private readonly Counter<long> _lockRefreshes;
	private readonly Counter<long> _lockRefreshFailures;
	private readonly Counter<long> _lockRefreshWaits;
	private readonly Counter<long> _lockAdmissionProbes;
	private readonly Counter<long> _lockAdmissionRejections;
	private readonly Counter<long> _lockFanOutItems;
	private readonly Counter<long> _lockFanOutSucceeded;
	private readonly Counter<long> _lockFanOutThrottled;

	/// <summary>
	/// Initializes a new instance of the <see cref="CacheMetrics"/> class.
	/// </summary>
	/// <param name="meterFactory">The factory the host supplies.</param>
	public CacheMetrics(IMeterFactory meterFactory)
	{
		Ensure.NotNull(meterFactory);

		_meter = meterFactory.Create(MeterName);
		_hits = _meter.CreateCounter<long>("gitlfscache.hits", unit: ObjectUnit, description: "Objects served from the local store.");
		_misses = _meter.CreateCounter<long>("gitlfscache.misses", unit: ObjectUnit, description: "Objects fetched from upstream because they were not stored.");
		_bytesServedFromCache = _meter.CreateCounter<long>("gitlfscache.cache_bytes_served", unit: "By", description: "Bytes served from the local store.");
		_bytesFetchedUpstream = _meter.CreateCounter<long>("gitlfscache.upstream_bytes_fetched", unit: "By", description: "Bytes fetched from upstream.");
		_bytesUploaded = _meter.CreateCounter<long>("gitlfscache.upload_bytes_relayed", unit: "By", description: "Bytes relayed to upstream on upload.");
		_objectsStored = _meter.CreateCounter<long>("gitlfscache.objects_stored", unit: ObjectUnit, description: "Objects verified and published to the store.");
		_verificationFailures = _meter.CreateCounter<long>("gitlfscache.verification_failures", unit: ObjectUnit, description: "Transfers whose content did not hash to the expected object id.");
		_coalescedWaits = _meter.CreateCounter<long>("gitlfscache.coalesced_waits", unit: RequestUnit, description: "Requests that waited for another request's fetch instead of fetching themselves.");
		_rejectedTokens = _meter.CreateCounter<long>("gitlfscache.rejected_tokens", unit: RequestUnit, description: "Requests refused because their transfer token was invalid or expired.");
		_lockListHits = _meter.CreateCounter<long>("gitlfscache.lock_list_hits", unit: RequestUnit, description: "Lock listings answered from a snapshot without reaching upstream.");
		_lockRefreshes = _meter.CreateCounter<long>("gitlfscache.lock_refreshes", unit: "{walk}", description: "Lock listing walks performed against upstream.");
		_lockRefreshFailures = _meter.CreateCounter<long>("gitlfscache.lock_refresh_failures", unit: "{walk}", description: "Lock listing walks that did not produce a snapshot.");
		_lockRefreshWaits = _meter.CreateCounter<long>("gitlfscache.lock_refresh_waits", unit: RequestUnit, description: "Requests that waited for another request's lock listing walk.");
		_lockAdmissionProbes = _meter.CreateCounter<long>("gitlfscache.lock_admission_probes", unit: RequestUnit, description: "Single-page upstream calls made only to prove a credential may read a repository's locks.");
		_lockAdmissionRejections = _meter.CreateCounter<long>("gitlfscache.lock_admission_rejections", unit: RequestUnit, description: "Admission probes upstream refused.");
		_lockFanOutItems = _meter.CreateCounter<long>("gitlfscache.lock_fanout_items", unit: "{call}", description: "Individual lock calls attempted as part of a batched request, including retries.");
		_lockFanOutSucceeded = _meter.CreateCounter<long>("gitlfscache.lock_fanout_succeeded", unit: "{call}", description: "Individual lock calls upstream accepted.");
		_lockFanOutThrottled = _meter.CreateCounter<long>("gitlfscache.lock_fanout_throttled", unit: "{call}", description: "Lock calls upstream throttled, each pausing the whole upstream.");
	}

	/// <summary>Records an object served from the store.</summary>
	/// <param name="upstream">The upstream key, recorded as a tag.</param>
	/// <param name="bytes">How many bytes were served.</param>
	public void RecordHit(string upstream, long bytes)
	{
		KeyValuePair<string, object?> tag = new("upstream", upstream);
		_hits.Add(1, tag);
		_bytesServedFromCache.Add(bytes, tag);
	}

	/// <summary>Records an object fetched from upstream.</summary>
	/// <param name="upstream">The upstream key, recorded as a tag.</param>
	/// <param name="bytes">How many bytes were fetched.</param>
	public void RecordMiss(string upstream, long bytes)
	{
		KeyValuePair<string, object?> tag = new("upstream", upstream);
		_misses.Add(1, tag);
		_bytesFetchedUpstream.Add(bytes, tag);
	}

	/// <summary>Records an upload relayed to upstream.</summary>
	/// <param name="upstream">The upstream key, recorded as a tag.</param>
	/// <param name="bytes">How many bytes were relayed.</param>
	public void RecordUpload(string upstream, long bytes) =>
		_bytesUploaded.Add(bytes, new KeyValuePair<string, object?>("upstream", upstream));

	/// <summary>Records an object verified and published to the store.</summary>
	/// <param name="upstream">The upstream key, recorded as a tag.</param>
	public void RecordStored(string upstream) =>
		_objectsStored.Add(1, new KeyValuePair<string, object?>("upstream", upstream));

	/// <summary>Records content that did not hash to its expected object id.</summary>
	/// <param name="upstream">The upstream key, recorded as a tag.</param>
	public void RecordVerificationFailure(string upstream) =>
		_verificationFailures.Add(1, new KeyValuePair<string, object?>("upstream", upstream));

	/// <summary>Records a request that waited for another request's fetch.</summary>
	/// <param name="upstream">The upstream key, recorded as a tag.</param>
	public void RecordCoalescedWait(string upstream) =>
		_coalescedWaits.Add(1, new KeyValuePair<string, object?>("upstream", upstream));

	/// <summary>Records a refused transfer token.</summary>
	public void RecordRejectedToken() => _rejectedTokens.Add(1);

	/// <summary>Records a lock listing answered from a snapshot.</summary>
	/// <remarks>
	/// The ratio of this to <see cref="RecordLockRefresh"/> is the multiple by which upstream lock
	/// traffic has been reduced, and is the pair to watch for this subsystem the way hits and misses
	/// are the pair to watch for the object store.
	/// </remarks>
	/// <param name="upstream">The upstream key, recorded as a tag.</param>
	public void RecordLockListHit(string upstream) =>
		_lockListHits.Add(1, new KeyValuePair<string, object?>("upstream", upstream));

	/// <summary>Records a lock listing walk against upstream.</summary>
	/// <param name="upstream">The upstream key, recorded as a tag.</param>
	public void RecordLockRefresh(string upstream) =>
		_lockRefreshes.Add(1, new KeyValuePair<string, object?>("upstream", upstream));

	/// <summary>Records a lock listing walk that produced no snapshot.</summary>
	/// <param name="upstream">The upstream key, recorded as a tag.</param>
	public void RecordLockRefreshFailure(string upstream) =>
		_lockRefreshFailures.Add(1, new KeyValuePair<string, object?>("upstream", upstream));

	/// <summary>Records a request that waited for another request's listing walk.</summary>
	/// <param name="upstream">The upstream key, recorded as a tag.</param>
	public void RecordLockRefreshWait(string upstream) =>
		_lockRefreshWaits.Add(1, new KeyValuePair<string, object?>("upstream", upstream));

	/// <summary>Records a single-page call made only to prove a credential.</summary>
	/// <param name="upstream">The upstream key, recorded as a tag.</param>
	public void RecordLockAdmissionProbe(string upstream) =>
		_lockAdmissionProbes.Add(1, new KeyValuePair<string, object?>("upstream", upstream));

	/// <summary>Records an admission probe upstream refused.</summary>
	/// <param name="upstream">The upstream key, recorded as a tag.</param>
	public void RecordLockAdmissionRejected(string upstream) =>
		_lockAdmissionRejections.Add(1, new KeyValuePair<string, object?>("upstream", upstream));

	/// <summary>Records one attempted lock call within a batched request.</summary>
	/// <param name="upstream">The upstream key, recorded as a tag.</param>
	public void RecordLockFanOutItem(string upstream) =>
		_lockFanOutItems.Add(1, new KeyValuePair<string, object?>("upstream", upstream));

	/// <summary>Records one lock call upstream accepted.</summary>
	/// <param name="upstream">The upstream key, recorded as a tag.</param>
	public void RecordLockFanOutSucceeded(string upstream) =>
		_lockFanOutSucceeded.Add(1, new KeyValuePair<string, object?>("upstream", upstream));

	/// <summary>Records a lock call upstream throttled.</summary>
	/// <remarks>
	/// Anything but zero here means the configured concurrency is above what this forge tolerates. It
	/// is the signal to turn MaxFanOutConcurrency down.
	/// </remarks>
	/// <param name="upstream">The upstream key, recorded as a tag.</param>
	public void RecordLockFanOutThrottled(string upstream) =>
		_lockFanOutThrottled.Add(1, new KeyValuePair<string, object?>("upstream", upstream));

	/// <inheritdoc />
	public void Dispose() => _meter.Dispose();
}
