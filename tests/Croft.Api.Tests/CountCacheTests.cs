using Croft.Api.Data;
using Croft.Api.Models;
using Croft.Api.Services;
using Croft.Api.Tests.Fixtures;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Croft.Api.Tests;

/// <summary>
/// Brief 09b item 4: the public category/tag reads with PostCount are cached in the API and
/// invalidated on every replica through Postgres LISTEN/NOTIFY. Instance A in these tests runs
/// without the listener, so "the croft-cache-listener row in pg_stat_activity" the brief kills is
/// unique and belongs to instance B; A's own freshness comes from the write path clearing its
/// local cache at once (the same-instance case).
/// </summary>
public class CountCacheTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    [Fact]
    public async Task SecondIdenticalCategoryListRequest_OnOneInstance_RunsNoSqlCommands()
    {
        var counter = new SqlCommandCounter();
        await using var factory = CreateFactory(withCacheListener: false, counter);
        var admin = factory.CreateClient().WithRole("Admin");
        await CreateCategoryAsync(admin, "CountCache Solo");

        var anonymous = factory.CreateClient().AsAnonymous();
        var first = await GetCategoryCountsAsync(anonymous);
        first.Should().ContainValue(0, "the seeded category starts with no published posts");
        counter.Commands.Should().BeGreaterThanOrEqualTo(1, "the first request reads the database");

        counter.Reset();
        var second = await GetCategoryCountsAsync(anonymous);
        second.Should().BeEquivalentTo(first);
        counter.Commands.Should().Be(0, "a cache hit must not touch the database");
    }

    [Fact]
    public async Task PublishingThroughAnInstance_UpdatesThatInstancesOwnCachedCounts()
    {
        var counter = new SqlCommandCounter();
        await using var factory = CreateFactory(withCacheListener: false, counter);
        var admin = factory.CreateClient().WithRole("Admin");
        var anonymous = factory.CreateClient().AsAnonymous();
        var category = await CreateCategoryAsync(admin, "CountCache Solo Write");

        _ = await GetCategoryCountsAsync(anonymous);
        counter.Reset();
        _ = await GetCategoryCountsAsync(anonymous);
        counter.Commands.Should().Be(0, "the list must be cached before the write");

        await PublishPostAsync(admin, "CountCache Solo Write Post", category.Id, []);

        var counts = await GetCategoryCountsAsync(anonymous);
        counts[category.Slug].Should().Be(1, "a write invalidates this instance's own cache at once");
    }

    [Fact]
    public async Task PublishingThroughOneInstance_UpdatesTheOtherInstancesCachedCounts()
    {
        var bCounter = new SqlCommandCounter();
        await using var a = CreateFactory(withCacheListener: false);
        await using var b = CreateFactory(withCacheListener: true, bCounter);
        await WaitForListenerAsync(b);

        var adminA = a.CreateClient().WithRole("Admin");
        var anonymousB = b.CreateClient().AsAnonymous();
        var category = await CreateCategoryAsync(adminA, "CountCache Shared");

        // A post published in the category through A: B's cached count must move 0 -> 1.
        _ = await GetCategoryCountsAsync(anonymousB);
        bCounter.Reset();
        _ = await GetCategoryCountsAsync(anonymousB);
        bCounter.Commands.Should().Be(0, "B's list must be cached before the write");

        await PublishPostAsync(adminA, "CountCache Shared One", category.Id, []);
        await WaitUntilAsync(
            async () => (await GetCategoryCountsAsync(anonymousB)).GetValueOrDefault(category.Slug) == 1,
            TimeSpan.FromSeconds(5),
            "B's category count becomes 1 after A publishes");

        // A post with a new tag published through A: B's tag list must show the new tag.
        _ = await GetTagCountsAsync(anonymousB);
        bCounter.Reset();
        _ = await GetTagCountsAsync(anonymousB);
        bCounter.Commands.Should().Be(0, "B's tag list must be cached before the write");

        await PublishPostAsync(adminA, "CountCache Shared Two", null, ["countcache-shared-tag"]);
        await WaitUntilAsync(
            async () => (await GetTagCountsAsync(anonymousB)).GetValueOrDefault("countcache-shared-tag") == 1,
            TimeSpan.FromSeconds(5),
            "B's tag list shows the new tag after A publishes");

        // A's scheduler publishing a due post: B's cached count must move 1 -> 2.
        _ = await GetCategoryCountsAsync(anonymousB);
        bCounter.Reset();
        _ = await GetCategoryCountsAsync(anonymousB);
        bCounter.Commands.Should().Be(0, "B's list must be cached before the scheduler publishes");

        await InsertScheduledDuePostAsync(category.Id, "countcache-due-post");
        await RunSchedulerOnceAsync(a);
        await WaitUntilAsync(
            async () => (await GetCategoryCountsAsync(anonymousB)).GetValueOrDefault(category.Slug) == 2,
            TimeSpan.FromSeconds(5),
            "B's category count becomes 2 after A's scheduler publishes");
    }

    [Fact]
    public async Task KillingTheListenerBackend_RecoversByClearingTheCacheAndLoggingTheReconnect()
    {
        var logs = new TestLogCapture();
        await using var a = CreateFactory(withCacheListener: false);
        await using var b = CreateFactory(withCacheListener: true, logs: logs);
        await WaitForListenerAsync(b);

        var adminA = a.CreateClient().WithRole("Admin");
        var anonymousB = b.CreateClient().AsAnonymous();
        var category = await CreateCategoryAsync(adminA, "CountCache Recovery");
        await PublishPostAsync(adminA, "CountCache Recovery One", category.Id, []);

        _ = await GetCategoryCountsAsync(anonymousB);
        (await GetCategoryCountsAsync(anonymousB))[category.Slug].Should().Be(1, "B caches the count before its listener dies");

        await KillListenerBackendAsync();

        await PublishPostAsync(adminA, "CountCache Recovery Two", category.Id, []);

        await WaitUntilAsync(
            async () => (await GetCategoryCountsAsync(anonymousB)).GetValueOrDefault(category.Slug) == 2,
            TimeSpan.FromSeconds(35),
            "B shows the new count within 35 s: its reconnect clears the whole cache");
        logs.Entries.Should().Contain(
            entry => entry.Level == LogLevel.Information && entry.Message.Contains("reconnected", StringComparison.Ordinal),
            "B must log the reconnect at Information");
    }

    [Fact]
    public async Task AdminGridRead_AfterCachedPublicList_RunsItsQueryAndSeesLiveCounts()
    {
        var counter = new SqlCommandCounter();
        await using var factory = CreateFactory(withCacheListener: false, counter);
        var admin = factory.CreateClient().WithRole("Admin");
        var anonymous = factory.CreateClient().AsAnonymous();
        var category = await CreateCategoryAsync(admin, "CountCache Admin");

        _ = await GetCategoryCountsAsync(anonymous);
        counter.Reset();
        _ = await GetCategoryCountsAsync(anonymous);
        counter.Commands.Should().Be(0, "the public list must be cached");

        // A write outside the API (no notification): only the never-cached admin read can see it.
        await InsertPublishedPostAsync(category.Id, "countcache-admin-direct");

        counter.Reset();
        var grid = await GetAdminGridAsync(admin);
        counter.Commands.Should().BeGreaterThanOrEqualTo(1, "the admin read runs its query");
        grid.Items.Single(item => item.Slug == category.Slug).PostCount
            .Should().Be(1, "the admin read sees live counts");
    }

    private BlogApiFactory CreateFactory(
        bool withCacheListener, DbCommandInterceptor? interceptor = null, TestLogCapture? logs = null)
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);

        return new BlogApiFactory(
            fixture.ConnectionString,
            storagePath,
            configureServices: services =>
            {
                // The scheduler and backfill would run their own SQL on their own schedule; these
                // tests drive the scheduler explicitly where the brief calls for it. The cache
                // listener is the one hosted service that must run.
                services.RemoveAll<IHostedService>();
                if (withCacheListener)
                    services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<CacheListenerService>());
                if (logs is not null)
                    services.AddSingleton<ILoggerProvider>(logs);
            },
            commandInterceptor: interceptor);
    }

    private static async Task WaitForListenerAsync(BlogApiFactory factory) =>
        await WaitUntilAsync(
            async () =>
            {
                await Task.Yield();
                return factory.Services.GetRequiredService<CacheListenerService>().BackendPid is not null;
            },
            TimeSpan.FromSeconds(30),
            "B's cache listener connects and finishes its setup");

    private static async Task RunSchedulerOnceAsync(BlogApiFactory factory)
    {
        var scheduler = new ScheduledPublisherHostedService(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ScheduledPublisherHostedService>.Instance,
            TimeProvider.System,
            factory.Services.GetRequiredService<IScheduledPublishWakeup>(),
            TestCache.NewNotifier());

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        await scheduler.PublishDuePostsAsync(db);
    }

    private async Task InsertScheduledDuePostAsync(Guid categoryId, string slug) =>
        await InsertPostAsync(categoryId, slug, PostStatus.Scheduled, DateTime.UtcNow.AddMinutes(-5));

    private async Task InsertPublishedPostAsync(Guid categoryId, string slug) =>
        await InsertPostAsync(categoryId, slug, PostStatus.Published, DateTime.UtcNow.AddHours(-1));

    private async Task InsertPostAsync(Guid categoryId, string slug, PostStatus status, DateTime publishedAt)
    {
        await using var db = new BlogDbContext(
            new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(fixture.ConnectionString).Options);
        db.Posts.Add(new Post
        {
            Slug = slug,
            Title = slug,
            MarkdownContent = "x",
            Status = status,
            Kind = PostKind.Post,
            CategoryId = categoryId,
            PublishedAt = publishedAt,
            CreatedAt = publishedAt,
            UpdatedAt = publishedAt,
            ContentEditedAt = publishedAt,
            AuthorId = "countcache-author",
            AuthorName = "Count Cache"
        });
        await db.SaveChangesAsync();
    }

    private async Task KillListenerBackendAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        // pg_stat_activity is server-wide and every test class's instance hosts its own listener,
        // so the row is only unique once scoped to this class's database (each class gets its own).
        const string scope = "application_name = 'croft-cache-listener' AND datname = current_database()";

        await using (var count = connection.CreateCommand())
        {
            count.CommandText = $"""
                SELECT count(*) FROM pg_stat_activity WHERE {scope}
                """;
            Convert.ToInt32(await count.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture).Should().Be(1,
                "instance A runs without a listener, so the only croft-cache-listener on this database is B's");
        }

        await using var kill = connection.CreateCommand();
        kill.CommandText = $"""
            SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE {scope}
            """;
        var terminated = Convert.ToBoolean(await kill.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        terminated.Should().BeTrue("the listener backend must exist to be killed");
    }

    private static async Task<CategoryDto> CreateCategoryAsync(HttpClient admin, string name)
    {
        var response = await admin.PostAsJsonAsync("/api/categories", name);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CategoryDto>())!;
    }

    private static async Task PublishPostAsync(HttpClient admin, string title, Guid? categoryId, IReadOnlyList<string> tags)
    {
        var created = await admin.PostAsJsonAsync("/api/posts",
            new CreatePostRequest(title, "## A short count-cache body", Tags: tags, CategoryId: categoryId));
        created.EnsureSuccessStatusCode();
        var post = (await created.Content.ReadFromJsonAsync<PostDto>())!;

        var published = await admin.PostAsJsonAsync($"/api/posts/{post.Id}/publish",
            new PublishRequest(DateTime.UtcNow.AddMinutes(-1)));
        published.EnsureSuccessStatusCode();
    }

    private static async Task<Dictionary<string, long>> GetCategoryCountsAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/categories");
        response.EnsureSuccessStatusCode();
        var categories = (await response.Content.ReadFromJsonAsync<CategoryDto[]>())!;
        return categories.ToDictionary(c => c.Slug, c => (long)c.PostCount);
    }

    private static async Task<Dictionary<string, long>> GetTagCountsAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/tags");
        response.EnsureSuccessStatusCode();
        var tags = (await response.Content.ReadFromJsonAsync<TagDto[]>())!;
        return tags.ToDictionary(t => t.Slug, t => (long)t.PostCount);
    }

    private static async Task<PagedResult<CategoryDto>> GetAdminGridAsync(HttpClient admin)
    {
        var response = await admin.GetAsync("/api/categories?page=1&pageSize=50");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PagedResult<CategoryDto>>())!;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        (await condition()).Should().BeTrue($"condition should have been met within {timeout.TotalSeconds:0} s: {what}");
    }
}
