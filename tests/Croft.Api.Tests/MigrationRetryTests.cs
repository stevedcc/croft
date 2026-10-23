using System.Net.Sockets;
using Croft.Api.Tests.Fixtures;
using Croft.Shared;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Croft.Api.Tests;

/// <summary>
/// The retry policy of the "migrate" entries, on a fake clock that follows the policy's
/// sleeps. Production evidence (2026-10-17): a database restart made the init containers throw
/// an unhandled exception and hang; the policy retries transient failures with a capped
/// exponential backoff and otherwise ends in a logged error and exit code 1, never a throw.
/// </summary>
public class MigrationRetryTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static NpgsqlException Refused() =>
        new("Failed to connect", new SocketException((int)SocketError.ConnectionRefused));

    // The shape production logged: the default execution strategy wraps the transient failure.
    private static InvalidOperationException Wrapped() =>
        new("An exception has been raised that is likely due to a transient failure.", Refused());

    [Fact]
    public async Task TransientFailures_AreRetriedWithBackoffOfOneTwoFourEightSixteenThenThirtySeconds()
    {
        var clock = new FakeTimerTimeProvider(Start);
        var logger = new ListLogger();
        var attempts = new List<TimeSpan>();

        var exitCode = await RunAsync(clock, TimeSpan.FromMinutes(10), logger, () =>
        {
            attempts.Add(clock.GetUtcNow() - Start);
            throw Wrapped();
        });

        exitCode.Should().Be(1);
        var gaps = attempts.Zip(attempts.Skip(1), (a, b) => b - a).Select(g => (int)g.TotalSeconds).ToList();
        gaps.Take(8).Should().Equal(1, 2, 4, 8, 16, 30, 30, 30);
        gaps.Should().OnlyContain(g => g <= 30);
    }

    [Fact]
    public async Task TransientFailuresThatOutlastTheTimeout_LogOneErrorWithTheLastException_AndExitWithCodeOne()
    {
        var clock = new FakeTimerTimeProvider(Start);
        var logger = new ListLogger();
        var attempts = 0;
        InvalidOperationException? last = null;

        var exitCode = await RunAsync(clock, TimeSpan.FromSeconds(60), logger, () =>
        {
            attempts++;
            throw last = Wrapped();
        });

        exitCode.Should().Be(1);
        (clock.GetUtcNow() - Start).Should().Be(TimeSpan.FromSeconds(60), "it keeps trying until the timeout, and no longer");
        attempts.Should().Be(7, "attempts at 0, 1, 3, 7, 15, 31 and 60 seconds");
        var errors = logger.Entries.Where(e => e.Level == LogLevel.Error).ToList();
        errors.Should().ContainSingle();
        errors[0].Exception.Should().BeSameAs(last);
    }

    [Fact]
    public async Task NonTransientFailure_IsNotRetried_LogsAnErrorAndExitsWithCodeOne()
    {
        var clock = new FakeTimerTimeProvider(Start);
        var logger = new ListLogger();
        var attempts = 0;
        var failure = new InvalidOperationException("column \"x\" of relation \"Posts\" already exists");

        var exitCode = await RunAsync(clock, TimeSpan.FromMinutes(5), logger, () =>
        {
            attempts++;
            throw failure;
        });

        exitCode.Should().Be(1);
        attempts.Should().Be(1);
        clock.GetUtcNow().Should().Be(Start, "a failing migration is not waited on");
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error && ReferenceEquals(e.Exception, failure));
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task SuccessOnTheThirdAttempt_ExitsWithCodeZero_AfterExactlyTwoWarnings()
    {
        var clock = new FakeTimerTimeProvider(Start);
        var logger = new ListLogger();
        var attempts = 0;

        var exitCode = await RunAsync(clock, TimeSpan.FromMinutes(5), logger, () =>
        {
            attempts++;
            return attempts < 3 ? throw Wrapped() : Task.CompletedTask;
        });

        exitCode.Should().Be(0);
        attempts.Should().Be(3);
        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToList();
        warnings.Should().HaveCount(2);
        warnings[0].Should().Contain("attempt 1").And.Contain("1s");
        warnings[1].Should().Contain("attempt 2").And.Contain("2s");
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Error);
    }

    [Fact]
    public void TransientClassification_FollowsNpgsqlAndTheWrappedExceptionChain()
    {
        MigrationRetry.IsTransient(Wrapped()).Should().BeTrue("production's wrapper around a refused connection");
        MigrationRetry.IsTransient(Refused()).Should().BeTrue();
        MigrationRetry.IsTransient(new TimeoutException()).Should().BeTrue();
        MigrationRetry.IsTransient(new PostgresException("", "FATAL", "FATAL", "57P03", "the database system is starting up"))
            .Should().BeTrue("a restarting server answers 57P03");
        MigrationRetry.IsTransient(new PostgresException("", "ERROR", "ERROR", "42701", "column already exists"))
            .Should().BeFalse("a migration that fails is not retried");
        MigrationRetry.IsTransient(new InvalidOperationException("boom")).Should().BeFalse();
    }

    [Fact]
    public void MigrateTimeout_DefaultsToFiveMinutes_ReadsATimeSpan_AndRejectsNonsenseNamingTheSetting()
    {
        MigrationRetry.ReadTimeout(Config(null)).Should().Be(TimeSpan.FromMinutes(5));
        MigrationRetry.ReadTimeout(Config("00:00:10")).Should().Be(TimeSpan.FromSeconds(10));
        foreach (var bad in new[] { "soon", "00:00:00", "-00:00:05" })
        {
            var act = () => MigrationRetry.ReadTimeout(Config(bad));
            act.Should().Throw<InvalidOperationException>().WithMessage("*Database:MigrateTimeout*");
        }
    }

    [Fact]
    public async Task EntryWithAnInvalidTimeoutSetting_LogsAnErrorNamingIt_AndExitsWithCodeOne_WithoutRunningTheMigration()
    {
        var logger = new ListLogger();
        var ran = false;

        var exitCode = await MigrationRetry.RunEntryAsync(
            Config("soon"), logger, TimeProvider.System, () => { ran = true; return Task.CompletedTask; });

        exitCode.Should().Be(1);
        ran.Should().BeFalse();
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error && e.Exception!.Message.Contains("Database:MigrateTimeout"));
    }

    private static IConfiguration Config(string? timeout) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(timeout is null ? [] : new Dictionary<string, string?> { ["Database:MigrateTimeout"] = timeout })
            .Build();

    /// <summary>Runs the policy, advancing the fake clock one second whenever it sleeps on it.</summary>
    private static async Task<int> RunAsync(
        FakeTimerTimeProvider clock, TimeSpan timeout, ILogger logger, Func<Task> migrate)
    {
        var run = MigrationRetry.RunAsync(migrate, timeout, logger, clock);
        while (!run.IsCompleted)
        {
            var sleeping = clock.WaitUntilSleepingAsync(TimeSpan.FromSeconds(10));
            if (await Task.WhenAny(run, sleeping) == run)
            {
                break;
            }

            clock.Advance(TimeSpan.FromSeconds(1));
        }

        return await run;
    }

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
