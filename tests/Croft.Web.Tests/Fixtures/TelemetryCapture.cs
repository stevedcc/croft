using System.Collections;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Croft.Web.Tests.Fixtures;

/// <summary>
/// Captures what the telemetry setup exports — spans, metric snapshots and log records — through
/// in-memory exporters, so tests assert on exported data exactly as a collector would receive it.
/// Exports arrive on the exporters' own threads, so the collections are thread-safe and the
/// visible lists are snapshots. Metric collection is explicit (<see cref="CollectMetrics"/>) so
/// no test waits on a timer.
/// </summary>
internal sealed class TelemetryCapture
{
    /// <summary>
    /// The endpoint value that makes the host register telemetry. Nothing listens on 127.0.0.1:9,
    /// so any OTLP attempt is refused at once and the tests need no collector.
    /// </summary>
    public const string EnabledEndpoint = "http://127.0.0.1:9";

    private readonly SynchronizedCollection<Activity> _spans = [];
    private readonly SynchronizedCollection<Metric> _metrics = [];
    private readonly SynchronizedCollection<LogRecord> _logs = [];

    public IReadOnlyList<Activity> Spans => _spans.Snapshot();

    public IReadOnlyList<Metric> Metrics => _metrics.Snapshot();

    public IReadOnlyList<LogRecord> Logs => _logs.Snapshot();

    public void Register(IServiceCollection services)
    {
        services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddInMemoryExporter(_spans))
            .WithMetrics(metrics => metrics.AddInMemoryExporter(_metrics));
        services.AddLogging(logging => logging.AddOpenTelemetry(options => options.AddInMemoryExporter(_logs)));
    }

    public static void CollectMetrics(IServiceProvider services)
        => services.GetRequiredService<MeterProvider>().ForceFlush();

    /// <summary>
    /// Waits until a span matching the predicate has been exported. The server-side span ends just
    /// after the client sees the response, so tests wait for the concrete condition (the span
    /// arriving in the capture) instead of assuming timing.
    /// </summary>
    public async Task<Activity> WaitForSpanAsync(Func<Activity, bool> predicate, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var span in Spans)
            {
                if (predicate(span))
                {
                    return span;
                }
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"no exported span matched: {because}");
    }

    /// <summary>
    /// Collects metrics and waits until a matching one has been exported. Framework code records
    /// measurements on its own schedule (a circuit finishes opening asynchronously), so tests
    /// wait for the concrete condition — the metric arriving in the capture.
    /// </summary>
    public async Task<Metric> WaitForMetricAsync(
        IServiceProvider services, Func<Metric, bool> predicate, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            CollectMetrics(services);
            foreach (var metric in Metrics)
            {
                if (predicate(metric))
                {
                    return metric;
                }
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"no exported metric matched: {because}");
    }

    /// <summary>
    /// Waits until a log record matching the predicate has been exported, so assertions about
    /// exported logs are never made against an empty, not-yet-flushed capture.
    /// </summary>
    public async Task<LogRecord> WaitForLogAsync(Func<LogRecord, bool> predicate, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var log in Logs)
            {
                if (predicate(log))
                {
                    return log;
                }
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"no exported log record matched: {because}");
    }

    /// <summary>
    /// Every exported span, log and metric attribute as "key=value", for privacy assertions that
    /// must search everything a collector would see.
    /// </summary>
    public IEnumerable<string> AllAttributeValues()
    {
        foreach (var span in Spans)
        {
            foreach (var (key, value) in span.TagObjects)
            {
                yield return $"{key}={value}";
            }
        }

        foreach (var log in Logs)
        {
            if (log.Attributes is null)
            {
                continue;
            }

            foreach (var (key, value) in log.Attributes)
            {
                yield return $"{key}={value}";
            }
        }

        foreach (var metric in Metrics)
        {
            foreach (var point in metric.GetMetricPoints())
            {
                foreach (var tag in point.Tags)
                {
                    yield return $"{tag.Key}={tag.Value}";
                }
            }
        }
    }

    /// <summary>An ICollection the exporters can write to from their own threads.</summary>
    private sealed class SynchronizedCollection<T> : ICollection<T>
    {
        private readonly Lock _gate = new();
        private readonly List<T> _items = [];

        public int Count { get { lock (_gate) { return _items.Count; } } }

        public bool IsReadOnly => false;

        public void Add(T item) { lock (_gate) { _items.Add(item); } }

        public void Clear() { lock (_gate) { _items.Clear(); } }

        public bool Contains(T item) { lock (_gate) { return _items.Contains(item); } }

        public void CopyTo(T[] array, int arrayIndex) { lock (_gate) { _items.CopyTo(array, arrayIndex); } }

        public bool Remove(T item) { lock (_gate) { return _items.Remove(item); } }

        public IEnumerator<T> GetEnumerator() { lock (_gate) { return ((IEnumerable<T>)[.. _items]).GetEnumerator(); } }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>An atomic copy — reads and copies under one lock, so an export arriving
        /// mid-snapshot cannot resize the list between the two.</summary>
        public IReadOnlyList<T> Snapshot() { lock (_gate) { return [.. _items]; } }
    }
}
