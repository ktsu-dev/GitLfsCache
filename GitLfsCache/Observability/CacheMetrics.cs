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

	/// <summary>
	/// Initializes a new instance of the <see cref="CacheMetrics"/> class.
	/// </summary>
	/// <param name="meterFactory">The factory the host supplies.</param>
	public CacheMetrics(IMeterFactory meterFactory)
	{
		Ensure.NotNull(meterFactory);

		_meter = meterFactory.Create(MeterName);
		_hits = _meter.CreateCounter<long>("gitlfscache.hits", unit: "{object}", description: "Objects served from the local store.");
		_misses = _meter.CreateCounter<long>("gitlfscache.misses", unit: "{object}", description: "Objects fetched from upstream because they were not stored.");
		_bytesServedFromCache = _meter.CreateCounter<long>("gitlfscache.cache_bytes_served", unit: "By", description: "Bytes served from the local store.");
		_bytesFetchedUpstream = _meter.CreateCounter<long>("gitlfscache.upstream_bytes_fetched", unit: "By", description: "Bytes fetched from upstream.");
		_bytesUploaded = _meter.CreateCounter<long>("gitlfscache.upload_bytes_relayed", unit: "By", description: "Bytes relayed to upstream on upload.");
		_objectsStored = _meter.CreateCounter<long>("gitlfscache.objects_stored", unit: "{object}", description: "Objects verified and published to the store.");
		_verificationFailures = _meter.CreateCounter<long>("gitlfscache.verification_failures", unit: "{object}", description: "Transfers whose content did not hash to the expected object id.");
		_coalescedWaits = _meter.CreateCounter<long>("gitlfscache.coalesced_waits", unit: "{request}", description: "Requests that waited for another request's fetch instead of fetching themselves.");
		_rejectedTokens = _meter.CreateCounter<long>("gitlfscache.rejected_tokens", unit: "{request}", description: "Requests refused because their transfer token was invalid or expired.");
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

	/// <inheritdoc />
	public void Dispose() => _meter.Dispose();
}
