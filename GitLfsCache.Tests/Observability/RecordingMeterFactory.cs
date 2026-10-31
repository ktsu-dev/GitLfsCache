// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Observability;

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

/// <summary>
/// A meter factory that totals what its own meters record, so a test can assert on a counter.
/// </summary>
/// <remarks>
/// The listener only follows meters this factory created. Every <see cref="ktsu.GitLfsCache.Observability.CacheMetrics"/>
/// shares one meter name, so filtering by name would also count measurements from tests running in
/// parallel.
/// </remarks>
internal sealed class RecordingMeterFactory : IMeterFactory
{
	private readonly ConcurrentDictionary<Meter, byte> _meters = new();
	private readonly ConcurrentDictionary<(string Instrument, string? Upstream), long> _totals = new();
	private readonly MeterListener _listener = new();

	public RecordingMeterFactory()
	{
		_listener.InstrumentPublished = (instrument, listener) =>
		{
			if (_meters.ContainsKey(instrument.Meter))
			{
				listener.EnableMeasurementEvents(instrument);
			}
		};

		_listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
		{
			string? upstream = null;
			foreach (KeyValuePair<string, object?> tag in tags)
			{
				if (tag.Key == "upstream")
				{
					upstream = tag.Value as string;
				}
			}

			_totals.AddOrUpdate((instrument.Name, upstream), value, (_, total) => total + value);
		});

		_listener.Start();
	}

	/// <summary>The total recorded on an instrument for one upstream tag.</summary>
	/// <param name="instrument">The instrument name.</param>
	/// <param name="upstream">The upstream tag value.</param>
	/// <returns>The sum of every measurement, or zero if none were recorded.</returns>
	public long Total(string instrument, string upstream) =>
		_totals.TryGetValue((instrument, upstream), out long total) ? total : 0;

	public Meter Create(MeterOptions options)
	{
		Meter meter = new(options);
		_meters.TryAdd(meter, 0);
		return meter;
	}

	public void Dispose()
	{
		_listener.Dispose();

		foreach (Meter meter in _meters.Keys)
		{
			meter.Dispose();
		}
	}
}
