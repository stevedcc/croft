using Microsoft.Extensions.Logging;

namespace Croft.Api.Tests.Fixtures;

/// <summary>
/// Captures everything the test host logs, so tests can assert on what was logged
/// (e.g. that an expected no-op produced nothing above Information).
/// </summary>
internal sealed class LogCollector : ILoggerProvider
{
    private readonly Lock _gate = new();
    private readonly List<LogEntry> _entries = [];

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    public ILogger CreateLogger(string categoryName) => new CollectorLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Add(LogEntry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
        }
    }

    private sealed class CollectorLogger(LogCollector collector, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => collector.Add(new LogEntry(
                logLevel,
                category,
                formatter(state, exception),
                exception,
                // Copied now: a state object is only valid during this call (the pooled LoggerMessageState is emptied after it).
                (state as IEnumerable<KeyValuePair<string, object?>>)?.ToList()));
    }
}

internal sealed record LogEntry(
    LogLevel Level,
    string Category,
    string Message,
    Exception? Exception,
    IEnumerable<KeyValuePair<string, object?>>? Values = null)
{
    /// <summary>When the entry was written, so a diagnosis can say how long an attempt took.</summary>
    public DateTimeOffset At { get; } = DateTimeOffset.UtcNow;
}
