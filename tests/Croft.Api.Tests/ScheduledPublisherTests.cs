using Croft.Api.Data;
using Croft.Api.Models;
using Croft.Api.Services;
using Croft.Api.Tests.Fixtures;
using Croft.Shared.Enums;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Croft.Api.Tests;

public class ScheduledPublisherTests(PostgresDatabaseFixture fixture)
    : IClassFixture<PostgresDatabaseFixture>
{
    [Fact]
    public async Task PublishesOnlyDuePosts()
    {
        var connectionString = fixture.ConnectionString;
        var services = new ServiceCollection();
        services.AddDbContext<BlogDbContext>(options => options.UseNpgsql(connectionString));
        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();

            db.Posts.Add(new Post
            {
                Id = Guid.NewGuid(),
                Slug = "due-post",
                Title = "Due",
                MarkdownContent = "x",
                Status = PostStatus.Scheduled,
                PublishedAt = DateTime.UtcNow.AddMinutes(-5),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                AuthorId = "a",
                AuthorName = "A"
            });

            db.Posts.Add(new Post
            {
                Id = Guid.NewGuid(),
                Slug = "future-post",
                Title = "Future",
                MarkdownContent = "x",
                Status = PostStatus.Scheduled,
                PublishedAt = DateTime.UtcNow.AddHours(1),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                AuthorId = "a",
                AuthorName = "A"
            });

            await db.SaveChangesAsync();
        }

        var publisher = new ScheduledPublisherHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ScheduledPublisherHostedService>.Instance,
            new FakeTimeProvider(DateTimeOffset.UtcNow),
            new ScheduledPublishWakeup(), TestCache.NewNotifier());

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
            await publisher.PublishDuePostsAsync(db);
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
            var due = await db.Posts.AsNoTracking().FirstAsync(p => p.Slug == "due-post");
            var future = await db.Posts.AsNoTracking().FirstAsync(p => p.Slug == "future-post");

            due.Status.Should().Be(PostStatus.Published);
            future.Status.Should().Be(PostStatus.Scheduled);
        }
    }

    [Fact]
    public async Task TwoInstancesTickingTogether_PublishEachDuePostOnce_AndLogOnceInTotal()
    {
        var now = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        var time = new FakeTimeProvider(new DateTimeOffset(now));
        var collector = new LogCollector();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(collector));
        var logger = loggerFactory.CreateLogger<ScheduledPublisherHostedService>();

        const string slugPrefix = "ha-concurrent";
        var postIds = new List<Guid>();
        await using (var setup = BuildProvider(fixture.ConnectionString))
        await using (var scope = setup.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
            for (var i = 0; i < 20; i++)
            {
                var id = Guid.CreateVersion7();
                postIds.Add(id);
                db.Posts.Add(new Post
                {
                    Id = id,
                    Slug = $"{slugPrefix}-{i}",
                    Title = $"Concurrent {i}",
                    MarkdownContent = "x",
                    Status = PostStatus.Scheduled,
                    PublishedAt = now.AddMinutes(-5),
                    CreatedAt = now.AddMinutes(-10),
                    UpdatedAt = now.AddMinutes(-10),
                    AuthorId = "a",
                    AuthorName = "A"
                });
            }

            await db.SaveChangesAsync();
        }

        // Each instance's publish write is held until the other instance has got as far as its
        // own write (its tick has therefore already read the due posts) or its tick is over,
        // so the two ticks overlap on the rows without any timing guesses. The tick that loses
        // the job lock never writes, and its completion releases the other one.
        var gateA = new HeldCommandInterceptor();
        var gateB = new HeldCommandInterceptor();
        var tickAFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tickBFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateA.HoldUntil(() => Task.WhenAny(gateB.Arrived, tickBFinished.Task));
        gateB.HoldUntil(() => Task.WhenAny(gateA.Arrived, tickAFinished.Task));

        await using var providerA = BuildProvider(fixture.ConnectionString, gateA);
        await using var providerB = BuildProvider(fixture.ConnectionString, gateB);

        var tickA = RunTickAsync(providerA, time, logger);
        var tickB = RunTickAsync(providerB, time, logger);
        _ = tickA.ContinueWith(static (task, state) => ((TaskCompletionSource)state!).TrySetResult(),
            tickAFinished, TaskScheduler.Default);
        _ = tickB.ContinueWith(static (task, state) => ((TaskCompletionSource)state!).TrySetResult(),
            tickBFinished, TaskScheduler.Default);

        await Task.WhenAll(tickA, tickB).WaitAsync(TimeSpan.FromSeconds(30));

        await using (var scope = providerA.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
            var rows = await db.Posts.AsNoTracking()
                .Where(p => p.Slug.StartsWith(slugPrefix))
                .ToListAsync();

            rows.Should().HaveCount(20, "the test created 20 due posts");
            rows.Should().OnlyContain(p => p.Status == PostStatus.Published,
                "both instances together must publish every due post exactly once");
            // Both instances share one clock on purpose: a tick logs the rows carrying ITS stamp, so with the
            // job lock broken (or a row written by both) the other instance's rows would carry the same
            // stamp and be logged twice: that is what the log count below catches. The stamp itself cannot
            // tell the two writers apart (review M-5 suggested a second clock; with one, a broken lock
            // goes unnoticed: each tick would log only its own stamp).
            rows.Should().OnlyContain(p => p.UpdatedAt == now, "every row carries the one stamp both ticks used");
        }

        // Scoped to this test's posts: the class database carries the other tests' posts too,
        // and a due leftover from them is correctly published and logged by the winner.
        var logged = collector.Entries
            .Where(e => e.Message.StartsWith("Published scheduled post", StringComparison.Ordinal))
            .Where(e => postIds.Any(id => e.Message.Contains(id.ToString(), StringComparison.Ordinal)))
            .ToList();
        logged.Should().HaveCount(20,
            "each due post must be published and logged exactly once in total across both instances");
        foreach (var id in postIds)
        {
            logged.Count(e => e.Message.Contains(id.ToString(), StringComparison.Ordinal))
                .Should().Be(1, $"post {id} must appear in the publish log exactly once");
        }
    }

    [Fact]
    public async Task Tick_ReturnsSkipped_WhileAnotherInstanceHoldsTheJobLock()
    {
        var now = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        var time = new FakeTimeProvider(new DateTimeOffset(now));
        var collector = new LogCollector();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(collector));
        var logger = loggerFactory.CreateLogger<ScheduledPublisherHostedService>();

        const string slug = "ha-lock-skip";
        await using var provider = BuildProvider(fixture.ConnectionString);
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
            db.Posts.Add(new Post
            {
                Id = Guid.CreateVersion7(),
                Slug = slug,
                Title = "Lock skip",
                MarkdownContent = "x",
                Status = PostStatus.Scheduled,
                PublishedAt = now.AddMinutes(-5),
                CreatedAt = now.AddMinutes(-10),
                UpdatedAt = now.AddMinutes(-10),
                AuthorId = "a",
                AuthorName = "A"
            });
            await db.SaveChangesAsync();
        }

        // Another instance is mid-tick: its transaction holds the publisher job's advisory lock.
        await using var holder = new NpgsqlConnection(fixture.ConnectionString);
        await holder.OpenAsync();
        await using var heldTransaction = await holder.BeginTransactionAsync();
        await using (var command = holder.CreateCommand())
        {
            command.Transaction = heldTransaction;
            command.CommandText = "SELECT pg_try_advisory_xact_lock(@jobId)";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@jobId";
            parameter.Value = BackgroundJobIds.ScheduledPublisher;
            command.Parameters.Add(parameter);
            var acquired = await command.ExecuteScalarAsync();
            acquired.Should().Be(true, "this test's transaction must hold the publisher job's lock");
        }

        var result = await RunTickAsync(provider, time, logger);

        result.Skipped.Should().BeTrue("an instance that does not get the job lock must skip the tick");
        result.Published.Should().BeEmpty("a skipped tick publishes nothing");

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
            var row = await db.Posts.AsNoTracking().SingleAsync(p => p.Slug == slug);
            row.Status.Should().Be(PostStatus.Scheduled, "a skipped tick must leave due posts alone");
        }

        collector.Entries.Should().NotContain(
            e => e.Message.StartsWith("Published scheduled post", StringComparison.Ordinal),
            "a skipped tick must not log any publish lines");

        // The post is deliberately left Scheduled; take it out again because the other tests in
        // this class run against the same database and correctly publish every due post they see.
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
            await db.Posts.Where(p => p.Slug == slug).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Tick_DoesNotPublishOrLogAPostAnotherInstancePublishedMidTick()
    {
        var now = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        var otherInstanceStamp = new DateTime(2026, 3, 1, 11, 59, 0, DateTimeKind.Utc);
        var time = new FakeTimeProvider(new DateTimeOffset(now));
        var collector = new LogCollector();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(collector));
        var logger = loggerFactory.CreateLogger<ScheduledPublisherHostedService>();

        var targetId = Guid.CreateVersion7();
        const string targetSlug = "ha-midtick-0";
        var others = new List<(Guid Id, string Slug)>
        {
            (Guid.CreateVersion7(), "ha-midtick-1"),
            (Guid.CreateVersion7(), "ha-midtick-2")
        };
        await using (var setup = BuildProvider(fixture.ConnectionString))
        await using (var scope = setup.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
            foreach (var (id, slug) in new[] { (targetId, targetSlug) }.Concat(others))
            {
                db.Posts.Add(new Post
                {
                    Id = id,
                    Slug = slug,
                    Title = slug,
                    MarkdownContent = "x",
                    Status = PostStatus.Scheduled,
                    PublishedAt = now.AddMinutes(-5),
                    CreatedAt = now.AddMinutes(-10),
                    UpdatedAt = now.AddMinutes(-10),
                    AuthorId = "a",
                    AuthorName = "A"
                });
            }

            await db.SaveChangesAsync();
        }

        // Hold the tick's publish write until another instance has published one of the due
        // posts the tick has just read (the gate opens at the write, so the read has happened).
        var gate = new HeldCommandInterceptor();
        var foreignWriteDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.HoldUntil(() => foreignWriteDone.Task);

        await using var provider = BuildProvider(fixture.ConnectionString, gate);
        var tick = RunTickAsync(provider, time, logger);

        await gate.Arrived.WaitAsync(TimeSpan.FromSeconds(30));
        await using (var foreign = BuildProvider(fixture.ConnectionString))
        await using (var scope = foreign.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
            var row = await db.Posts.SingleAsync(p => p.Id == targetId);
            row.Status = PostStatus.Published;
            row.UpdatedAt = otherInstanceStamp;
            await db.SaveChangesAsync();
        }

        foreignWriteDone.TrySetResult();

        var result = await tick.WaitAsync(TimeSpan.FromSeconds(30));

        result.Skipped.Should().BeFalse();
        result.Published.Select(p => p.Id).Should().BeEquivalentTo(others.Select(o => o.Id),
            "the tick must report only the posts it changed itself");

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
            var target = await db.Posts.AsNoTracking().SingleAsync(p => p.Id == targetId);
            target.Status.Should().Be(PostStatus.Published);
            target.UpdatedAt.Should().Be(otherInstanceStamp,
                "a post another instance published must not be published a second time");

            foreach (var (id, _) in others)
            {
                var row = await db.Posts.AsNoTracking().SingleAsync(p => p.Id == id);
                row.Status.Should().Be(PostStatus.Published);
                row.UpdatedAt.Should().Be(now);
            }
        }

        // Scoped to this test's posts: the class database carries the other tests' posts too.
        var logged = collector.Entries
            .Where(e => e.Message.StartsWith("Published scheduled post", StringComparison.Ordinal))
            .Where(e => e.Message.Contains(targetId.ToString(), StringComparison.Ordinal)
                || others.Any(o => e.Message.Contains(o.Id.ToString(), StringComparison.Ordinal)))
            .ToList();
        logged.Should().HaveCount(2, "only the two posts this tick published are logged");
        logged.Should().NotContain(e => e.Message.Contains(targetId.ToString(), StringComparison.Ordinal),
            "a post another instance changed must never be logged by this one");
    }

    [Fact]
    public async Task ExecuteAsync_CatchesNonCancellationFailures_AndKeepsServiceAlive()
    {
        var interceptor = new ThrowingCommandInterceptor(new InvalidOperationException("simulated query failure"));
        var connectionString = fixture.ConnectionString;
        var services = new ServiceCollection();
        services.AddDbContext<BlogDbContext>(options => options.UseNpgsql(connectionString)
            .AddInterceptors(interceptor));
        await using var provider = services.BuildServiceProvider();

        using var cts = new CancellationTokenSource();
        var publisher = new TestablePublisher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        var run = publisher.Run(cts.Token);
        await interceptor.Threw.Task.WaitAsync(TimeSpan.FromSeconds(30));
        cts.Cancel();

        var ex = await Record.ExceptionAsync(() => run);
        ex.Should().BeAssignableTo<OperationCanceledException>(
            "a failure inside the loop must be caught and logged so one bad tick cannot kill the service");
        interceptor.ExecutionCount.Should().Be(1, "the failing tick must actually have run");
    }

    [Fact]
    public async Task ExecuteAsync_LetsCancellationPropagate()
    {
        var interceptor = new ThrowingCommandInterceptor(new OperationCanceledException());
        var connectionString = fixture.ConnectionString;
        var services = new ServiceCollection();
        services.AddDbContext<BlogDbContext>(options => options.UseNpgsql(connectionString)
            .AddInterceptors(interceptor));
        await using var provider = services.BuildServiceProvider();

        var publisher = new TestablePublisher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        // Bounded so a swallowed cancellation shows up as a failed assertion, not a hung run.
        var run = () => publisher.Run(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        await run.Should().ThrowAsync<OperationCanceledException>(
            "cancellation is how the host stops the loop; swallowing it would report a shutdown as an error");
        interceptor.ExecutionCount.Should().Be(1, "the cancelled tick must actually have run");
    }

    // BackgroundService.StartAsync only returns the loop's task when it is already completed,
    // so tests await the loop directly to see what a tick's failure does to it.
    private sealed class TestablePublisher(IServiceScopeFactory scopeFactory, TimeProvider time)
        : ScheduledPublisherHostedService(scopeFactory, NullLogger<ScheduledPublisherHostedService>.Instance, time, new ScheduledPublishWakeup(), TestCache.NewNotifier())
    {
        public Task Run(CancellationToken stoppingToken) => ExecuteAsync(stoppingToken);
    }

    private static ServiceProvider BuildProvider(string connectionString, params IInterceptor[] interceptors)
    {
        var services = new ServiceCollection();
        services.AddDbContext<BlogDbContext>(options =>
        {
            options.UseNpgsql(connectionString);
            options.AddInterceptors(interceptors);
        });
        return services.BuildServiceProvider();
    }

    private static async Task<PublishDuePostsResult> RunTickAsync(
        IServiceProvider provider, TimeProvider time, ILogger<ScheduledPublisherHostedService> logger)
    {
        var publisher = new ScheduledPublisherHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(), logger, time, new ScheduledPublishWakeup(), TestCache.NewNotifier());
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        return await publisher.PublishDuePostsAsync(db);
    }
}
