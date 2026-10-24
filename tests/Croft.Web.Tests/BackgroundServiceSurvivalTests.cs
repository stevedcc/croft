using AwesomeAssertions;
using Croft.Web.Data;
using Croft.Web.Options;
using Croft.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Croft.Web.Tests;

/// <summary>
/// Brief 16a item 2: an exception that escapes a <see cref="BackgroundService"/>'s
/// <c>ExecuteAsync</c> stops the host (<c>BackgroundServiceExceptionBehavior.StopHost</c>, the
/// default), and since .NET 11 the host then exits with a failure. The Web app's session purges
/// catch and log a failed tick and tick again: these tests run the real hosted services on a
/// manual clock with every tick failing, and check that the failure was logged as an Error, that
/// the service is still running, and that the next tick happens once the interval has passed.
/// (The security.txt tick has its own test in <see cref="SecurityTxtHostedServiceTests"/>.)
/// </summary>
public class BackgroundServiceSurvivalTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SessionPurge_KeepsTicking_AfterAFailedTick()
    {
        var ticks = new FailingTicks();
        await using var provider = new ServiceCollection()
            .AddScoped<CroftWebDbContext>(_ => ticks.Fail<CroftWebDbContext>())
            .BuildServiceProvider();
        var clock = new ManualTimeProvider(Now);
        var logger = new CapturingLogger<SessionPurgeHostedService>();
        using var service = new SessionPurgeHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(), logger, clock, new AuthSettings(TimeSpan.FromDays(30)));

        await AssertSurvivesFailedTicksAsync(service, clock, ticks, logger.Entries, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task InMemorySessionPurge_KeepsTicking_AfterAFailedTick()
    {
        var ticks = new FailingTicks();
        // The store reads its own clock in PurgeExpired: failing it fails the tick.
        var store = new InMemoryTokenStore(new FailingClock(ticks));
        var clock = new ManualTimeProvider(Now);
        var logger = new CapturingLogger<InMemorySessionPurgeHostedService>();
        using var service = new InMemorySessionPurgeHostedService(store, logger, clock);

        await AssertSurvivesFailedTicksAsync(service, clock, ticks, logger.Entries, TimeSpan.FromMinutes(1));
    }

    private static async Task AssertSurvivesFailedTicksAsync<T>(
        BackgroundService service, ManualTimeProvider clock, FailingTicks ticks,
        List<CapturingLogger<T>.Entry> logs, TimeSpan interval)
    {
        await service.StartAsync(CancellationToken.None);
        await ticks.WaitForAsync(1);
        // The loop either sleeps until the next tick or has died: whichever comes first.
        await Task.WhenAny(clock.WaitForTimersAsync(1), service.ExecuteTask!);

        logs.Should().Contain(e => e.Level == LogLevel.Error && e.Exception != null, "a failed tick is logged as an Error");
        service.ExecuteTask.Should().NotBeNull();
        service.ExecuteTask!.IsCompleted.Should().BeFalse("a failed tick must not end the service (and with it the host)");

        await clock.AdvanceAsync(interval, expectTimers: 2);
        ticks.Count.Should().Be(2, "the loop ticked again after the failed tick");
        service.ExecuteTask.IsCompleted.Should().BeFalse("the second failed tick does not end the service either");

        await service.StopAsync(CancellationToken.None);
        service.ExecuteTask.IsCanceled.Should().BeTrue("the service ends only when the host stops it");
    }

    /// <summary>Counts the ticks that reached their dependency and fails every one of them.</summary>
    private sealed class FailingTicks
    {
        private readonly Lock _gate = new();
        private readonly List<(int Count, TaskCompletionSource Signal)> _waiters = [];
        private int _count;

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _count;
                }
            }
        }

        public TResult Fail<TResult>()
        {
            lock (_gate)
            {
                _count++;
                foreach (var waiter in _waiters.Where(w => _count >= w.Count))
                {
                    waiter.Signal.TrySetResult();
                }
            }

            throw new InvalidOperationException("simulated tick failure");
        }

        /// <summary>Completes once <paramref name="count"/> ticks have failed; bounded so a loop that died fails the test.</summary>
        public Task WaitForAsync(int count)
        {
            lock (_gate)
            {
                if (_count >= count)
                {
                    return Task.CompletedTask;
                }

                var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((count, signal));
                return signal.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
        }
    }

    /// <summary>A clock that fails every read, counted as a failed tick.</summary>
    private sealed class FailingClock(FailingTicks ticks) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => ticks.Fail<DateTimeOffset>();
    }
}
