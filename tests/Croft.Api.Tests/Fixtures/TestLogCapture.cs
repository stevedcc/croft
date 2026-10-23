using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Croft.Api.Tests.Fixtures;

/// <summary>Captures log entries so a test can assert what an instance logged.</summary>
internal sealed class TestLogCapture : ILoggerProvider
{
    public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new TestLogger(Entries);

    public void Dispose()
    {
    }

    private sealed class TestLogger(ConcurrentQueue<(LogLevel Level, string Message)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }
}
