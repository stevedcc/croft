using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Croft.Web.Tests;

/// <summary>
/// Brief 11a item 2: the daily security.txt tick runs in <see cref="SecurityTxtHostedService"/>.
/// These tests drive its loop (a controllable clock, no real waiting) and pin that it is
/// registered whether or not the Web database is configured.
/// </summary>
public class SecurityTxtHostedServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void HostedService_IsRegistered_WithoutTheWebDatabase()
    {
        // Database-less mode is a supported configuration; the daily warning must not depend on it.
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("Blog:BaseUrl", "https://example.com/");
        });

        factory.Services.GetServices<IHostedService>().Should().ContainSingle(s => s is SecurityTxtHostedService,
            "the checker is registered unconditionally, so its daily tick must be too");
    }

    [Fact]
    public async Task Loop_WarnsOncePerDay_WhileTheConditionHolds()
    {
        var clock = new ManualTimeProvider(Start);
        var logger = new ListLogger();
        var checker = new SecurityTxtChecker(clock, new ListLogger<SecurityTxtChecker>(logger));
        checker.Check("Contact: mailto:security@example.com\nExpires: 2026-01-11T00:00:00Z");
        logger.Warnings.Should().HaveCount(1, "the startup check warns once");
        using var service = new SecurityTxtHostedService(checker, new ListLogger<SecurityTxtHostedService>(new ListLogger()), clock);

        await service.StartAsync(CancellationToken.None);
        await clock.WaitForTimersAsync(1);
        logger.Warnings.Should().HaveCount(1, "the first tick on the startup day repeats nothing");

        await clock.AdvanceAsync(TimeSpan.FromDays(1), expectTimers: 2);
        logger.Warnings.Should().HaveCount(2, "one more Warning a day later");

        await clock.AdvanceAsync(TimeSpan.FromDays(1), expectTimers: 3);
        logger.Warnings.Should().HaveCount(3, "and another the day after");

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Loop_LogsAnErrorAndKeepsTicking_WhenATickThrows()
    {
        var clock = new ManualTimeProvider(Start);
        var checkerLogger = new ListLogger<SecurityTxtChecker>(new ListLogger()) { ThrowOnLog = false };
        var checker = new SecurityTxtChecker(clock, checkerLogger);
        checker.Check("Contact: mailto:security@example.com\nExpires: 2026-01-11T00:00:00Z");
        var serviceLogger = new CapturingLogger<SecurityTxtHostedService>();
        using var service = new SecurityTxtHostedService(checker, serviceLogger, clock);

        await service.StartAsync(CancellationToken.None);
        await clock.WaitForTimersAsync(1);

        checkerLogger.ThrowOnLog = true;
        await clock.AdvanceAsync(TimeSpan.FromDays(1), expectTimers: 2);
        serviceLogger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error && e.Exception != null,
            "a failing tick is logged, not swallowed silently and not fatal");

        checkerLogger.ThrowOnLog = false;
        await clock.AdvanceAsync(TimeSpan.FromDays(1), expectTimers: 3);
        checkerLogger.Inner.Warnings.Should().HaveCount(2,
            "after the failing tick the loop ticked again and warned (startup + the third day)");

        await service.StopAsync(CancellationToken.None);
    }

    /// <summary>An <see cref="ILogger{T}"/> over a <see cref="ListLogger"/> that can throw on demand.</summary>
    private sealed class ListLogger<T>(ListLogger inner) : ILogger<T>
    {
        public ListLogger Inner { get; } = inner;

        public bool ThrowOnLog { get; set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (ThrowOnLog)
            {
                throw new InvalidOperationException("the log sink failed");
            }

            Inner.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
