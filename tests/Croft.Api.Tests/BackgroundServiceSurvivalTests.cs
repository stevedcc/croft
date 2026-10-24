using AwesomeAssertions;
using Croft.Api.Data;
using Croft.Api.Services;
using Croft.Api.Social;
using Croft.Api.Tests.Fixtures;
using Croft.Api.Tests.Fixtures.Social;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Croft.Api.Tests;

/// <summary>
/// Brief 16a item 2: an exception that escapes a <see cref="BackgroundService"/>'s
/// <c>ExecuteAsync</c> stops the host (<c>BackgroundServiceExceptionBehavior.StopHost</c>, the
/// default), and since .NET 11 the host then exits with a failure. Each looping service catches
/// and logs a failed tick and ticks again. These tests make every tick fail (the tick's
/// <see cref="BlogDbContext"/> cannot be resolved), run the real hosted service on a fake clock,
/// and check that the failure was logged as an Error, that the service is still running and that
/// the next tick happens once the interval has passed.
/// </summary>
public class BackgroundServiceSurvivalTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public Task SocialAnnounce_KeepsTicking_AfterAFailedTick() =>
        AssertSurvivesFailedTicksAsync(SocialAnnounceHostedService.TickInterval, (scopes, time, logger) =>
            new SocialAnnounceHostedService(
                logger.CreateLogger<SocialAnnounceHostedService>(), scopes, time, TestCache.NewNotifier(),
                Options.Create(new SocialOptions()), [], new SocialPlatformGates(), new FakeStorage(), new NoHandles()));

    [Fact]
    public Task SocialFetch_KeepsTicking_AfterAFailedTick() =>
        AssertSurvivesFailedTicksAsync(SocialFetchHostedService.TickInterval, (scopes, time, logger) =>
            new SocialFetchHostedService(
                logger.CreateLogger<SocialFetchHostedService>(), scopes, time, TestCache.NewNotifier(),
                Options.Create(new SocialOptions()), [], new SocialPlatformGates(), new SocialNoAvatars()));

    [Fact]
    public Task PageViewRetention_KeepsTicking_AfterAFailedTick() =>
        AssertSurvivesFailedTicksAsync(TimeSpan.FromHours(24), (scopes, time, logger) =>
            new PageViewRetentionHostedService(
                scopes, logger.CreateLogger<PageViewRetentionHostedService>(), time, new ConfigurationBuilder().Build()));

    private static async Task AssertSurvivesFailedTicksAsync(
        TimeSpan interval, Func<IServiceScopeFactory, TimeProvider, ILoggerFactory, BackgroundService> create)
    {
        var ticks = new FailingTicks();
        await using var provider = new ServiceCollection()
            .AddScoped<BlogDbContext>(_ => ticks.Fail())
            .BuildServiceProvider();
        var time = new FakeTimerTimeProvider(Start);
        var logs = new LogCollector();
        using var loggers = LoggerFactory.Create(builder => builder.AddProvider(logs));
        using var service = create(provider.GetRequiredService<IServiceScopeFactory>(), time, loggers);

        await service.StartAsync(CancellationToken.None);
        await ticks.WaitForAsync(1);
        // The loop either sleeps until the next tick or has died: whichever comes first.
        await Task.WhenAny(time.WaitUntilSleepingAsync(TimeSpan.FromSeconds(30)), service.ExecuteTask!);

        logs.Entries.Should().Contain(e => e.Level == LogLevel.Error, "a failed tick is logged as an Error");
        service.ExecuteTask.Should().NotBeNull();
        service.ExecuteTask!.IsCompleted.Should().BeFalse("a failed tick must not end the service (and with it the host)");

        time.Advance(interval);
        await ticks.WaitForAsync(2);
        await time.WaitUntilSleepingAsync(TimeSpan.FromSeconds(30));
        service.ExecuteTask.IsCompleted.Should().BeFalse("the second failed tick does not end the service either");

        await service.StopAsync(CancellationToken.None);
        service.ExecuteTask.IsCanceled.Should().BeTrue("the service ends only when the host stops it");
    }

    /// <summary>Counts the ticks that reached the database and fails every one of them.</summary>
    private sealed class FailingTicks
    {
        private readonly Lock _gate = new();
        private readonly List<(int Count, TaskCompletionSource Signal)> _waiters = [];
        private int _count;

        public BlogDbContext Fail()
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
}
