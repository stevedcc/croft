using Microsoft.AspNetCore.Http;
using Croft.Api.Data;
using Croft.Api.Models;
using Croft.Api.Services;
using Croft.Api.Tests.Fixtures;
using Croft.Api.Tests.Fixtures.Social;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Croft.Api.Tests;

/// <summary>
/// Brief 09b item 2: the scheduler sleeps until the next scheduled post is due (capped at
/// 5 minutes) instead of polling every minute forever, and a singleton wakeup signal lets the
/// post service wake it the moment a post is scheduled, rescheduled or unscheduled. The
/// multi-replica guarantees of PublishDuePostsAsync/BackgroundJobLock.TryRunAsync are unchanged
/// and stay covered by ScheduledPublisherTests.
/// </summary>
public class ScheduledPublisherSleepTests(PostgresDatabaseFixture fixture)
    : IClassFixture<PostgresDatabaseFixture>
{
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTime StartUtc = Start.UtcDateTime;
    private static readonly TimeSpan RealTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task WithNoScheduledPosts_OverThirtySimulatedMinutes_TheSchedulerRunsSixToSevenTimes()
    {
        var time = new FakeTimerTimeProvider(Start);
        var signal = new SqlCommandSignal("""Posts""");
        await using var provider = BuildProvider(fixture.ConnectionString, signal);
        using var cts = new CancellationTokenSource();
        var scheduler = new TestableScheduler(
            provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<ScheduledPublisherHostedService>.Instance, time);

        var run = scheduler.Run(cts.Token);
        try
        {
            // Each tick reads the due posts and the earliest scheduled PublishedAt: 2 matching
            // commands per tick with nothing scheduled.
            await time.WaitUntilSleepingAsync(RealTimeout);

            for (var minute = 1; minute <= 30; minute++)
            {
                // Settle before every step: the loop must be asleep on the fake clock again, so
                // a tick's scheduling can never race the clock's next jump.
                await time.WaitUntilSleepingAsync(RealTimeout);
                time.Advance(TimeSpan.FromMinutes(1));
            }

            await time.WaitUntilSleepingAsync(RealTimeout);
        }
        finally
        {
            await StopAsync(cts, run);
        }

        var ticks = signal.Count / 2;
        ticks.Should().BeInRange(6, 7,
            "with nothing scheduled the scheduler sleeps the 5-minute cap, so 30 simulated minutes cost 6-7 ticks, not 30");
    }

    [Fact]
    public async Task APostScheduledAtTPlus90Seconds_WhileTheSchedulerSleepsItsCap_IsPublishedAtTPlus90()
    {
        var time = new FakeTimerTimeProvider(Start);
        var wakeup = new ScheduledPublishWakeup();
        await using var provider = BuildProvider(fixture.ConnectionString);
        using var cts = new CancellationTokenSource();
        var scheduler = new TestableScheduler(
            provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<ScheduledPublisherHostedService>.Instance, time, wakeup);

        var run = scheduler.Run(cts.Token);
        try
        {
            await time.WaitUntilSleepingAsync(RealTimeout);

            // The post service schedules a post for T+90 s: this replica's own trigger must wake
            // the scheduler out of its 5-minute cap so the post publishes on time.
            Guid postId;
            await using (var scope = provider.CreateAsyncScope())
            {
                var postsDb = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
                var posts = new PostService(
                    postsDb,
                    new SlugService(),
                    null!,
                    NullLogger<PostService>.Instance,
                    time,
                    wakeup,
                    TestCache.NewNotifier(),
                    TestCache.NewCache(),
                    new HttpContextAccessor(),
                    new DateTimeNormalizer(time),
                    new NoSocialLinks(), NoSocialLinks.Options, NoSocialLinks.Outbox(postsDb));
                var created = await posts.CreateAsync(new CreatePostRequest("Wake-up Test", "body"), "author-id", "Author");
                var published = await posts.PublishAsync(created.Id, new PublishRequest(StartUtc + TimeSpan.FromSeconds(90)));
                published!.Status.Should().Be(PostStatus.Scheduled);
                postId = created.Id;
            }

            // Small steps, so a tick fires with the clock at its own due instant: one 90 s jump
            // would let an intermediate minute-tick see the overshoot clock and publish the post
            // without the wakeup, hiding exactly the behaviour under test.
            await AdvanceInStepsAsync(time, TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(10));
            var publishedAt = await WaitForPublishAsync(fixture.ConnectionString, postId);
            publishedAt.Should().BeWithin(TimeSpan.FromSeconds(1)).Before(StartUtc + TimeSpan.FromSeconds(90),
                "scheduling the post woke the scheduler, which then slept exactly until T+90 s");
        }
        finally
        {
            await StopAsync(cts, run);
        }
    }

    [Fact]
    public async Task APostDueAtTPlusThreeMinutes_ThisReplicaDidNotSchedule_IsPublishedByTPlusFiveMinutes()
    {
        var time = new FakeTimerTimeProvider(Start);
        await using var provider = BuildProvider(fixture.ConnectionString);
        using var cts = new CancellationTokenSource();
        var scheduler = new TestableScheduler(
            provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<ScheduledPublisherHostedService>.Instance, time);

        var run = scheduler.Run(cts.Token);
        try
        {
            await time.WaitUntilSleepingAsync(RealTimeout);

            // Inserted directly — no wakeup will fire, so the 5-minute cap is what covers it.
            var postId = Guid.CreateVersion7();
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
                db.Posts.Add(new Post
                {
                    Id = postId,
                    Slug = $"unscheduled-by-us-{postId:N}",
                    Title = "Foreign scheduled post",
                    MarkdownContent = "x",
                    Status = PostStatus.Scheduled,
                    PublishedAt = StartUtc + TimeSpan.FromMinutes(3),
                    CreatedAt = StartUtc,
                    UpdatedAt = StartUtc,
                    AuthorId = "a",
                    AuthorName = "A"
                });
                await db.SaveChangesAsync();
            }

            // Steps again (30 × 10 s): the cap timer must fire at its own +5 min instant.
            await AdvanceInStepsAsync(time, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(10));

            var publishedAt = await WaitForPublishAsync(fixture.ConnectionString, postId);
            publishedAt.Should().BeOnOrBefore(StartUtc + TimeSpan.FromMinutes(5),
                "a post another replica scheduled is covered by the sleep cap: 5 minutes at the latest");
            publishedAt.Should().BeOnOrAfter(StartUtc + TimeSpan.FromMinutes(3),
                "a scheduled post is never published before its due time");
        }
        finally
        {
            await StopAsync(cts, run);
        }
    }

    /// <summary>Advances the fake clock in equal small steps, letting the loop settle between
    /// steps, so every tick fires with the clock at its own due instant.</summary>
    private static async Task AdvanceInStepsAsync(FakeTimerTimeProvider time, TimeSpan total, TimeSpan step)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < total; elapsed += step)
        {
            await time.WaitUntilSleepingAsync(RealTimeout);
            time.Advance(step);
        }

        await time.WaitUntilSleepingAsync(RealTimeout);
    }

    /// <summary>Waits on the concrete condition — the row is published — with a real-time bound,
    /// so a scheduler that never publishes fails the test instead of hanging it. The short poll
    /// interval is between condition checks only.</summary>
    private static async Task<DateTime> WaitForPublishAsync(string connectionString, Guid postId)
    {
        var deadline = DateTime.UtcNow + RealTimeout;
        while (true)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """SELECT "Status", "UpdatedAt" FROM "Posts" WHERE "Id" = $1""";
            // Unnamed (positional) parameter: Npgsql binds it to the $1 placeholder.
            var parameter = command.CreateParameter();
            parameter.Value = postId;
            command.Parameters.Add(parameter);
            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync() && (PostStatus)reader.GetInt32(0) == PostStatus.Published)
            {
                return reader.GetDateTime(1);
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"Post {postId} was not published within {RealTimeout}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
    }

    private static async Task StopAsync(CancellationTokenSource cts, Task run)
    {
        cts.Cancel();
        try
        {
            await run;
        }
        catch (OperationCanceledException)
        {
            // How the host stops the loop; the tests await it so a failing tick cannot escape.
        }
    }

    private static ServiceProvider BuildProvider(string connectionString, params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors)
    {
        var services = new ServiceCollection();
        services.AddDbContext<BlogDbContext>(options =>
        {
            options.UseNpgsql(connectionString);
            options.AddInterceptors(interceptors);
        });
        return services.BuildServiceProvider();
    }

    // BackgroundService.StartAsync only returns the loop's task when it is already completed,
    // so tests await the loop directly (the pattern of ScheduledPublisherTests.TestablePublisher).
    private sealed class TestableScheduler(
        IServiceScopeFactory scopeFactory,
        ILogger<ScheduledPublisherHostedService> logger,
        TimeProvider time,
        IScheduledPublishWakeup? wakeup = null,
        ICacheNotifier? cacheNotifier = null)
        : ScheduledPublisherHostedService(scopeFactory, logger, time, wakeup ?? new ScheduledPublishWakeup(),
            cacheNotifier ?? TestCache.NewNotifier())
    {
        public Task Run(CancellationToken stoppingToken) => ExecuteAsync(stoppingToken);
    }
}
