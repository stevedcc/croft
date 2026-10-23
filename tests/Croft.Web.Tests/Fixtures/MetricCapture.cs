using System.Diagnostics.Metrics;

namespace Croft.Web.Tests.Fixtures;

/// <summary>
/// Records every measurement the named meters emit, synchronously at the point of recording — no
/// host, no exporters, no timers. These counters are process-wide statics, so the tests using this
/// capture run in a collection that opts out of parallel execution: a neighbouring class firing the
/// same counter would corrupt exact-count assertions.
/// </summary>
internal sealed class MetricCapture(params string[] meterNames) : IDisposable
{
    private readonly MeterListener _listener = new();

    public List<Sample> Measurements { get; } = [];

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
            Measurements.Add(new(instrument.Name, value, tags.ToArray())));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            Measurements.Add(new(instrument.Name, value, tags.ToArray())));
        _listener.Start();
        return this;
    }

    public void Dispose() => _listener.Dispose();

    /// <summary>Records the observable instruments' current values — gauges read live state at
    /// collection time, so a test collects explicitly instead of waiting on a timer.</summary>
    public void RecordObservable() => _listener.RecordObservableInstruments();

    public sealed record Sample(string Instrument, double Value, KeyValuePair<string, object?>[] Tags)
    {
        public bool HasTag(string key, object? value) => Tags.Any(t => t.Key == key && Equals(t.Value, value));
    }
}
