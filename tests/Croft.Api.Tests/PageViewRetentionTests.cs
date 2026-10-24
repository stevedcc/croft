using Croft.Api.Data;
using Croft.Api.Models;
using Croft.Api.Services;
using Croft.Api.Tests.Fixtures;
using Croft.Shared.Enums;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Data.Common;

namespace Croft.Api.Tests;

/// <summary>
/// Brief 09b item 5: PageViews rows older than Croft:Analytics:RetentionDays (default 90) are
/// deleted — once a day, in batches of at most 5,000 rows per statement, under the job lock so
/// one replica deletes and the others skip — and Posts.ViewCount totals stay.
/// </summary>
public class PageViewRetentionTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private static readonly DateTimeOffset Today = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Purge_DeletesOnlyRowsMoreThanNinetyDaysOld_AndKeepsViewCountTotals()
    {
        var day = DateOnly.FromDateTime(Today.UtcDateTime);
        var post = await AddPostAsync("retention-ninety", viewCount: 7);
        await AddPageViewsAsync(1, "retention-ninety-91", day.AddDays(-91), post.Id);
        await AddPageViewsAsync(1, "retention-ninety-90", day.AddDays(-90), post.Id);
        await AddPageViewsAsync(1, "retention-ninety-1", day.AddDays(-1), post.Id);

        var result = await CreateService().PurgeAsync(CreateDb());

        result.Skipped.Should().BeFalse();
        result.Deleted.Should().Be(1, "only the row more than 90 days old goes; day -90 is exactly 90 days before today");
        (await OwnDaysAsync("retention-ninety")).Should().BeEquivalentTo(
            [day.AddDays(-90), day.AddDays(-1)]);
        (await ViewCountAsync(post.Id)).Should().Be(7, "the view totals are not touched");
    }

    [Fact]
    public async Task Purge_DeletesFromTheTableTheModelNames_NotAHardCodedOne()
    {
        // M-5: the model maps PageView to another table; the purge must follow it. The renamed table is a
        // copy of PageViews' columns holding one old row.
        var day = DateOnly.FromDateTime(Today.UtcDateTime);
        await using (var setup = CreateDb())
        {
            await setup.Database.ExecuteSqlRawAsync("""CREATE TABLE IF NOT EXISTS "PageViewsRenamed" (LIKE "PageViews")""");
            await setup.Database.ExecuteSqlRawAsync("""DELETE FROM "PageViewsRenamed" """);
            await setup.Database.ExecuteSqlAsync($"""
                INSERT INTO "PageViewsRenamed" ("Id", "PostId", "Timestamp", "Day", "IpHash")
                VALUES ({Guid.CreateVersion7()}, {Guid.CreateVersion7()}, {DateTime.UtcNow}, {day.AddDays(-200)}, 'renamed-old')
                """);
        }

        await using var renamed = AnalyticsTablesTests.NewDb<AnalyticsTablesTests.RenamedTablesDbContext>(fixture.ConnectionString);
        var result = await CreateService().PurgeAsync(renamed);

        result.Deleted.Should().Be(1, "the old row sits in the table the model names");
        await using var check = CreateDb();
        (await check.Database.SqlQuery<long>($"""SELECT count(*) AS "Value" FROM "PageViewsRenamed" """).SingleAsync())
            .Should().Be(0);
    }

    [Fact]
    public async Task Purge_HonoursRetentionDaysThirty()
    {
        var day = DateOnly.FromDateTime(Today.UtcDateTime);
        var post = await AddPostAsync("retention-thirty", viewCount: 3);
        await AddPageViewsAsync(1, "retention-thirty-31", day.AddDays(-31), post.Id);
        await AddPageViewsAsync(1, "retention-thirty-30", day.AddDays(-30), post.Id);

        var result = await CreateService(retentionDays: 30).PurgeAsync(CreateDb());

        result.Skipped.Should().BeFalse();
        result.Deleted.Should().Be(1);
        (await OwnDaysAsync("retention-thirty")).Should().BeEquivalentTo([day.AddDays(-30)]);
    }

    [Fact]
    public async Task Purge_DeletesTwelveThousandRows_InAtLeastThreeStatements()
    {
        var day = DateOnly.FromDateTime(Today.UtcDateTime);
        var post = await AddPostAsync("retention-batches", viewCount: 1);
        await AddPageViewsAsync(12_000, "retention-batches-old", day.AddDays(-100), post.Id);

        var counter = new DeleteCommandCounter();
        var result = await CreateService().PurgeAsync(CreateDb(counter));

        result.Skipped.Should().BeFalse();
        result.Deleted.Should().Be(12_000);
        counter.DeleteCommands.Should().BeGreaterThanOrEqualTo(3,
            "12,000 rows at 5,000 per statement needs at least three DELETEs");
        (await OwnDaysAsync("retention-batches")).Should().BeEmpty();
    }

    [Fact]
    public async Task TwoInstancesRunningAtTheSameMoment_OneDeletesOneSkips()
    {
        var day = DateOnly.FromDateTime(Today.UtcDateTime);
        var post = await AddPostAsync("retention-race", viewCount: 1);
        await AddPageViewsAsync(5, "retention-race-old", day.AddDays(-200), post.Id);

        // Instance 1's DELETE is held open, so its transaction keeps the job's advisory lock
        // held while instance 2 runs — the two purges are provably simultaneous. They are not
        // lined up on the lock probe itself: a losing tick issues no command after its probe
        // that a rendezvous gate could wait for.
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new HeldDeleteInterceptor();
        held.HoldUntil(released.Task);
        await using var firstDb = CreateDb(held);
        await using var secondDb = CreateDb();

        var firstRun = CreateService().PurgeAsync(firstDb);
        await held.Arrived.WaitAsync(TimeSpan.FromSeconds(30));

        var second = await CreateService().PurgeAsync(secondDb);
        released.TrySetResult();
        var first = await firstRun;

        first.Skipped.Should().BeFalse("instance 1 holds the job lock and does the work");
        first.Deleted.Should().Be(5);
        second.Skipped.Should().BeTrue("instance 2 finds the lock held and skips");
        second.Deleted.Should().Be(0);
        (await OwnDaysAsync("retention-race")).Should().BeEmpty();
    }

    private static PageViewRetentionHostedService CreateService(int? retentionDays = null)
    {
        var settings = new Dictionary<string, string?>();
        if (retentionDays is { } days)
            settings["Croft:Analytics:RetentionDays"] = days.ToString();

        return new PageViewRetentionHostedService(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PageViewRetentionHostedService>.Instance,
            new FakeTimeProvider(Today),
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    }

    private BlogDbContext CreateDb(params IInterceptor[] interceptors) => new(
        new DbContextOptionsBuilder<BlogDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .AddInterceptors(interceptors)
            .Options);

    private async Task<Post> AddPostAsync(string slug, int viewCount)
    {
        await using var db = CreateDb();
        var post = new Post
        {
            Slug = slug,
            Title = slug,
            MarkdownContent = "x",
            Status = PostStatus.Published,
            Kind = PostKind.Post,
            PublishedAt = Today.UtcDateTime.AddDays(-3),
            CreatedAt = Today.UtcDateTime.AddDays(-3),
            UpdatedAt = Today.UtcDateTime.AddDays(-3),
            ContentEditedAt = Today.UtcDateTime.AddDays(-3),
            AuthorId = "retention-author",
            AuthorName = "Retention",
            Views = new PostViewCount { ViewCount = viewCount }
        };
        db.Posts.Add(post);
        await db.SaveChangesAsync();
        return post;
    }

    private async Task AddPageViewsAsync(int count, string marker, DateOnly day, Guid postId)
    {
        await using var db = CreateDb();
        for (var i = 0; i < count; i++)
        {
            db.PageViews.Add(new PageView
            {
                PostId = postId,
                Timestamp = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                Day = day,
                IpHash = $"{marker}-{i}",
                UserAgent = "retention-test"
            });

            if (i % 2000 == 1999)
                await db.SaveChangesAsync();
        }

        await db.SaveChangesAsync();
    }

    private async Task<List<DateOnly>> OwnDaysAsync(string marker)
    {
        await using var db = CreateDb();
        return await db.PageViews
            .AsNoTracking()
            .Where(v => v.IpHash.StartsWith(marker))
            .Select(v => v.Day)
            .ToListAsync();
    }

    private async Task<long> ViewCountAsync(Guid postId)
    {
        await using var db = CreateDb();
        return (await db.PostViewCounts.AsNoTracking().SingleAsync(v => v.PostId == postId)).ViewCount;
    }

    /// <summary>
    /// Holds every DELETE a context issues until a test-controlled task completes — the
    /// HeldCommandInterceptor pattern for DELETE statements — so a test can stage the exact
    /// moment two retention ticks contend for the job lock.
    /// </summary>
    private sealed class HeldDeleteInterceptor : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task _holdUntil = Task.CompletedTask;

        /// <summary>Completes as soon as the first held DELETE has been reached.</summary>
        public Task Arrived => _arrived.Task;

        public void HoldUntil(Task condition) => _holdUntil = condition;

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("DELETE", StringComparison.Ordinal))
            {
                _arrived.TrySetResult();
                // Bounded so a gate that never releases surfaces as a failed test, not a hung run.
                await _holdUntil.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }

            return result;
        }
    }

    /// <summary>Counts the DELETE statements a purge sends, whatever else the run does.</summary>
    private sealed class DeleteCommandCounter : DbCommandInterceptor
    {
        private int _deleteCommands;

        public int DeleteCommands => Volatile.Read(ref _deleteCommands);

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Count(command);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Count(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Count(DbCommand command)
        {
            if (command.CommandText.Contains("DELETE", StringComparison.Ordinal))
                Interlocked.Increment(ref _deleteCommands);
        }
    }
}
