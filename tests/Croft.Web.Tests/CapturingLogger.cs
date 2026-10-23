using Microsoft.Extensions.Logging;

namespace Croft.Web.Tests;

/// <summary>Records every log entry with its level, formatted text and structured properties.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<Entry> Entries { get; } = [];

    /// <summary>Every formatted message, property value and exception text, joined: what a reader of the logs could see.</summary>
    public string AllText => string.Join('\n', Entries.Select(e => e.Text));

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var properties = state is IEnumerable<KeyValuePair<string, object?>> pairs
            ? pairs.Where(p => p.Key != "{OriginalFormat}").ToDictionary(p => p.Key, p => p.Value)
            : [];
        Entries.Add(new Entry(logLevel, formatter(state, exception), properties, exception));
    }

    internal sealed record Entry(LogLevel Level, string Message, Dictionary<string, object?> Properties, Exception? Exception)
    {
        public string Text => string.Join(' ',
            [Message, .. Properties.Values.Select(v => v?.ToString()), Exception?.ToString()]);
    }
}
