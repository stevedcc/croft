using System.Diagnostics.Metrics;

namespace Croft.Api.Tests.Fixtures;

/// <summary>
/// Records every measurement the named meters emit, synchronously at the point of recording — no
/// host, no exporters, no timers. These counters are process-wide statics, so the tests using this
/// capture run in a collection that opts out of parallel execution: a neighbouring class firing the
/// same counter would corrupt exact-count assertions.
/// </summary>
internal sealed class MetricCapture(params string[] meterNames) : IDisposable
{
    private readonly MeterListener _listener = new();

    private readonly List<Sample> _measurements = [];

    /// <summary>A snapshot of what has been recorded so far (measurements can arrive from other threads).</summary>
    public IReadOnlyList<Sample> Measurements
    {
        get { lock (_measurements) return [.. _measurements]; }
    }

    private void Record(Sample sample)
    {
        lock (_measurements)
            _measurements.Add(sample);
    }

    public MetricCapture Start()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (meterNames.Contains(instrument.Meter.Name))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            Record(new(instrument.Name, value, tags.ToArray())));
        _listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) =>
            Record(new(instrument.Name, value, tags.ToArray())));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            Record(new(instrument.Name, value, tags.ToArray())));
        _listener.Start();
        return this;
    }

    /// <summary>Reads every observable instrument (gauges) once, as an exporter's collection pass would.</summary>
    public void Observe() => _listener.RecordObservableInstruments();

    public void Dispose() => _listener.Dispose();

    public sealed record Sample(string Instrument, double Value, KeyValuePair<string, object?>[] Tags)
    {
        public bool HasTag(string key, object? value) => Tags.Any(t => t.Key == key && Equals(t.Value, value));
    }
}
