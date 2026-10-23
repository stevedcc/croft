using Microsoft.Extensions.Logging;

namespace Croft.Web.Tests;

/// <summary>A minimal <see cref="ILogger"/> that records Information and Warning lines so tests
/// can assert on what would have been written to the operator's logs. Errors and below are
/// dropped — the project's tests look at Information (one-line "X happened") and Warning (the
/// brief's "the site must not fail startup or readiness over X" rule) only.</summary>
internal sealed class ListLogger : ILogger
{
    public List<string> Information { get; } = [];

    public List<string> Warnings { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var line = formatter(state, exception);
        switch (logLevel)
        {
            case LogLevel.Information:
                Information.Add(line);
                break;
            case LogLevel.Warning:
                Warnings.Add(line);
                break;
        }
    }
}