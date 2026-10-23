using Croft.Api.Data;
using Croft.Api.Models;
using Croft.Api.Services;
using Croft.Api.Tests.Fixtures;
using Croft.Shared.Enums;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Croft.Api.Tests;

public class PostSummaryBackfillTests(PostgresDatabaseFixture fixture)
    : IClassFixture<PostgresDatabaseFixture>
{
    [Fact]
    public async Task Backfill_FillsNullSummaries_AndIsIdempotent()
    {
        var connectionString = fixture.ConnectionString;
        var services = new ServiceCollection();
        services.AddDbContext<BlogDbContext>(options => options.UseNpgsql(connectionString));
        await using var provider = services.BuildServiceProvider();

        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var fixedUpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var longMarkdown = string.Join(' ', Enumerable.Repeat("word", 221));
        var expectedLongSummary = string.Join(' ', Enumerable.Repeat("word", 60)) + "…";

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();

            // Insert two posts with Summary = NULL via SQL.
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "Posts" ("Id", "Slug", "Title", "MarkdownContent", "Status", "CreatedAt", "UpdatedAt", "AuthorId", "AuthorName", "ViewCount")
                VALUES ({firstId}, 'one', 'One', {longMarkdown}, 0, {fixedUpdatedAt}, {fixedUpdatedAt}, 'a', 'A', 0)
                """);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "Posts" ("Id", "Slug", "Title", "MarkdownContent", "Status", "CreatedAt", "UpdatedAt", "AuthorId", "AuthorName", "ViewCount")
                VALUES ({secondId}, 'two', 'Two', {"# Two\n\nBeta text."}, 0, {fixedUpdatedAt}, {fixedUpdatedAt}, 'a', 'A', 0)
                """);
        }

        var backfill = new PostSummaryBackfillService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PostSummaryBackfillService>.Instance,
            TestCache.NewNotifier());

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
            await backfill.RunOnceAsync(db);
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
            var first = await db.Posts.AsNoTracking().SingleAsync(p => p.Id == firstId);
            var second = await db.Posts.AsNoTracking().SingleAsync(p => p.Id == secondId);

            first.Summary.Should().Be(expectedLongSummary);
            second.Summary.Should().Be("Beta text.");
            first.ReadingMinutes.Should().Be(2);
            second.ReadingMinutes.Should().Be(1);
            first.UpdatedAt.Should().Be(fixedUpdatedAt);
            second.UpdatedAt.Should().Be(fixedUpdatedAt);
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
            await backfill.RunOnceAsync(db);
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
            var first = await db.Posts.AsNoTracking().SingleAsync(p => p.Id == firstId);
            var second = await db.Posts.AsNoTracking().SingleAsync(p => p.Id == secondId);

            first.Summary.Should().Be(expectedLongSummary);
            second.Summary.Should().Be("Beta text.");
            first.UpdatedAt.Should().Be(fixedUpdatedAt);
            second.UpdatedAt.Should().Be(fixedUpdatedAt);
        }
    }

    [Fact]
    public async Task Backfill_ProcessesAllPosts_InBatchesOf100()
    {
        var connectionString = fixture.ConnectionString;
        var services = new ServiceCollection();
        services.AddDbContext<BlogDbContext>(options => options.UseNpgsql(connectionString));
        await using var provider = services.BuildServiceProvider();

        var now = DateTime.UtcNow;
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();

            for (var i = 0; i < 250; i++)
            {
                db.Posts.Add(new Post
                {
                    Id = Guid.NewGuid(),
                    Slug = $"post-{i}",
                    Title = $"Post {i}",
                    MarkdownContent = $"Numbered text {i}.",
                    Status = PostStatus.Draft,
                    CreatedAt = now,
                    UpdatedAt = now,
                    AuthorId = "a",
                    AuthorName = "A",
                    Summary = null
                });
            }

            await db.SaveChangesAsync();
        }

        var backfill = new PostSummaryBackfillService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PostSummaryBackfillService>.Instance,
            TestCache.NewNotifier());

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
            await backfill.RunOnceAsync(db);
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
            // Scoped to this test's batch ("post-N"): the class database also carries the
            // other tests' posts, which must not change this test's counts.
            var missing = await db.Posts.CountAsync(p => p.Slug.StartsWith("post-") && p.Summary == null);
            var filled = await db.Posts.CountAsync(p => p.Slug.StartsWith("post-") && p.Summary != null);

            missing.Should().Be(0);
            filled.Should().Be(250);
        }
    }

    [Fact]
    public async Task RunOnce_DoesNotOverwriteASummaryAnotherWriterFilledMidRun()
    {
        var connectionString = fixture.ConnectionString;
        var services = new ServiceCollection();
        services.AddDbContext<BlogDbContext>(options => options.UseNpgsql(connectionString));
        await using var provider = services.BuildServiceProvider();

        var targetId = Guid.CreateVersion7();
        const string targetSlug = "bf-midrun-0";
        var others = new List<(Guid Id, string Slug)>
        {
            (Guid.CreateVersion7(), "bf-midrun-1"),
            (Guid.CreateVersion7(), "bf-midrun-2")
        };
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
            foreach (var (id, slug) in new[] { (targetId, targetSlug) }.Concat(others))
            {
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO "Posts" ("Id", "Slug", "Title", "MarkdownContent", "Status", "CreatedAt", "UpdatedAt", "AuthorId", "AuthorName", "ViewCount")
                    VALUES ({id}, {slug}, {slug}, {"# Heading\n\nBeta text."}, 0, {DateTime.UtcNow}, {DateTime.UtcNow}, 'a', 'A', 0)
                    """);
            }
        }

        // Hold the backfill's writes until another writer has filled one of the summaries the
        // backfill has just read (the gate opens at the first write, so the read has happened).
        var gate = new HeldCommandInterceptor();
        var foreignWriteDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.HoldUntil(() => foreignWriteDone.Task);

        await using var gatedProvider = new ServiceCollection()
            .AddDbContext<BlogDbContext>(options => options.UseNpgsql(connectionString).AddInterceptors(gate))
            .BuildServiceProvider();
        var backfill = new PostSummaryBackfillService(
            gatedProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PostSummaryBackfillService>.Instance,
            TestCache.NewNotifier());
        await using (var scope = gatedProvider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
            var run = backfill.RunOnceAsync(db);

            await gate.Arrived.WaitAsync(TimeSpan.FromSeconds(30));
            await using (var foreign = provider.CreateAsyncScope())
            {
                var foreignDb = foreign.ServiceProvider.GetRequiredService<BlogDbContext>();
                var row = await foreignDb.Posts.SingleAsync(p => p.Id == targetId);
                row.Summary = "written elsewhere";
                row.ReadingMinutes = 7;
                await foreignDb.SaveChangesAsync();
            }

            foreignWriteDone.TrySetResult();

            var result = await run.WaitAsync(TimeSpan.FromSeconds(30));

            result.Skipped.Should().BeFalse();
            result.Filled.Should().Be(2, "only the two posts still missing a summary are filled by this run");
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
            var target = await db.Posts.AsNoTracking().SingleAsync(p => p.Id == targetId);
            target.Summary.Should().Be("written elsewhere",
                "a summary another writer filled in must never be overwritten");
            target.ReadingMinutes.Should().Be(7,
                "a summary another writer filled in must keep its reading minutes too");

            foreach (var (id, _) in others)
            {
                var row = await db.Posts.AsNoTracking().SingleAsync(p => p.Id == id);
                row.Summary.Should().Be("Beta text.");
                row.ReadingMinutes.Should().Be(1);
            }
        }
    }

    [Fact]
    public async Task RunOnce_ReturnsSkipped_WhileAnotherInstanceHoldsTheJobLock()
    {
        var connectionString = fixture.ConnectionString;
        var services = new ServiceCollection();
        services.AddDbContext<BlogDbContext>(options => options.UseNpgsql(connectionString));
        await using var provider = services.BuildServiceProvider();

        var postId = Guid.CreateVersion7();
        const string slug = "bf-lock-skip";
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "Posts" ("Id", "Slug", "Title", "MarkdownContent", "Status", "CreatedAt", "UpdatedAt", "AuthorId", "AuthorName", "ViewCount")
                VALUES ({postId}, {slug}, {slug}, {"# Heading\n\nBeta text."}, 0, {DateTime.UtcNow}, {DateTime.UtcNow}, 'a', 'A', 0)
                """);
        }

        // Another instance is mid-run: its transaction holds the backfill job's advisory lock.
        await using var holder = new NpgsqlConnection(fixture.ConnectionString);
        await holder.OpenAsync();
        await using var heldTransaction = await holder.BeginTransactionAsync();
        await using (var command = holder.CreateCommand())
        {
            command.Transaction = heldTransaction;
            command.CommandText = "SELECT pg_try_advisory_xact_lock(@jobId)";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@jobId";
            parameter.Value = BackgroundJobIds.PostSummaryBackfill;
            command.Parameters.Add(parameter);
            var acquired = await command.ExecuteScalarAsync();
            acquired.Should().Be(true, "this test's transaction must hold the backfill job's lock");
        }

        var backfill = new PostSummaryBackfillService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PostSummaryBackfillService>.Instance,
            TestCache.NewNotifier());
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
            var result = await backfill.RunOnceAsync(db);

            result.Skipped.Should().BeTrue("an instance that does not get the job lock must skip the run");
            result.Filled.Should().Be(0, "a skipped run fills nothing");
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
            var row = await db.Posts.AsNoTracking().SingleAsync(p => p.Id == postId);
            row.Summary.Should().BeNull("a skipped run must leave missing summaries alone");
        }

        // The post is deliberately left with a missing summary; take it out again so the other
        // tests in this class see only the posts they created.
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
            await db.Posts.Where(p => p.Id == postId).ExecuteDeleteAsync();
        }
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

        var backfill = new TestableBackfill(provider.GetRequiredService<IServiceScopeFactory>());

        var ex = await Record.ExceptionAsync(() => backfill.Run(CancellationToken.None));
        ex.Should().BeNull(
            "a failure during the one-shot backfill must be caught and logged, not fail host startup");
        interceptor.ExecutionCount.Should().Be(1, "the failing query must actually have run");
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

        var backfill = new TestableBackfill(provider.GetRequiredService<IServiceScopeFactory>());

        var run = () => backfill.Run(CancellationToken.None);
        await run.Should().ThrowAsync<OperationCanceledException>(
            "cancellation is how the host stops the backfill; swallowing it would report a shutdown as an error");
        interceptor.ExecutionCount.Should().Be(1, "the cancelled query must actually have run");
    }

    // BackgroundService.StartAsync only returns the run's task when it is already completed,
    // so tests await the run directly to see what a failure does to it.
    private sealed class TestableBackfill(IServiceScopeFactory scopeFactory)
        : PostSummaryBackfillService(scopeFactory, NullLogger<PostSummaryBackfillService>.Instance, TestCache.NewNotifier())
    {
        public Task Run(CancellationToken ct) => ExecuteAsync(ct);
    }

    [Fact]
    public async Task RecomputeMigration_ThenBackfill_ReplacesAnExistingPostsStaleSummary()
    {
        var services = new ServiceCollection();
        services.AddDbContext<BlogDbContext>(options => options.UseNpgsql(fixture.ConnectionString));
        await using var provider = services.BuildServiceProvider();

        var id = Guid.NewGuid();
        var at = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        const string Stale = "I built my cluster … the whole stack myself.…";
        const string Markdown = "Short paragraph. It ends in a full stop.";

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
            var migrator = db.GetService<IMigrator>();

            // Before the migration: the previous release's world, a stored summary ending ".…".
            await migrator.MigrateAsync("20261014150241_PublishedAtPartialIndexes");
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "Posts" ("Id", "Slug", "Title", "MarkdownContent", "Summary", "ReadingMinutes", "Status", "CreatedAt", "UpdatedAt", "AuthorId", "AuthorName", "ViewCount")
                VALUES ({id}, 'stale-summary', 'Stale', {Markdown}, {Stale}, 1, 0, {at}, {at}, 'a', 'A', 0)
                """);

            await migrator.MigrateAsync();

            (await db.Posts.AsNoTracking().SingleAsync(p => p.Id == id)).Summary
                .Should().BeNull("the migration clears every stored summary so the backfill recomputes it");
        }

        var notifier = new RecordingNotifier(provider, id);
        var backfill = new PostSummaryBackfillService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PostSummaryBackfillService>.Instance,
            notifier);
        await using (var scope = provider.CreateAsyncScope())
        {
            await backfill.RunOnceAsync(scope.ServiceProvider.GetRequiredService<BlogDbContext>());
        }

        notifier.Notified.Should().Equal([CacheGroup.All], "one invalidation for the whole recompute, not one per post");
        notifier.SummarySeenInAFreshContext.Should().Be(Markdown,
            "the notification fires after the commit: a fresh connection already sees the new summary");

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
            var post = await db.Posts.AsNoTracking().SingleAsync(p => p.Id == id);
            post.Summary.Should().Be(Markdown);
            post.UpdatedAt.Should().Be(at, "the recompute is invisible in the edit history");
        }
    }

    /// <summary>Records every invalidation and, at that moment, reads the post's summary through a
    /// fresh context: proof the notification came after the backfill's transaction committed.</summary>
    private sealed class RecordingNotifier(IServiceProvider provider, Guid postId) : ICacheNotifier
    {
        public List<CacheGroup> Notified { get; } = [];

        public string? SummarySeenInAFreshContext { get; private set; }

        public Task SaveAndNotifyAsync(BlogDbContext db, CacheGroup changed, CancellationToken ct = default) =>
            throw new NotSupportedException();

        // The NOTIFY joins the backfill's transaction; it is delivered when that commits.
        public Task NotifyAsync(DbContext db, CacheGroup changed, CancellationToken ct = default) => Task.CompletedTask;

        public async Task InvalidateLocalAsync(CacheGroup changed)
        {
            Notified.Add(changed);
            await using var scope = provider.CreateAsyncScope();
            var fresh = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
            SummarySeenInAFreshContext = (await fresh.Posts.AsNoTracking().SingleAsync(p => p.Id == postId)).Summary;
        }
    }
}
