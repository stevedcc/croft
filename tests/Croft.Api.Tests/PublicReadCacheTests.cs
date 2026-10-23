using Croft.Api.Data;
using Croft.Api.Models;
using Croft.Api.Services;
using Croft.Api.Tests.Fixtures;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using AwesomeAssertions;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using System.Net.Http.Json;

namespace Croft.Api.Tests;

/// <summary>
/// Brief 09d item 2: the anonymous variants of the six public post/page reads are cached in the
/// API and invalidated on every replica through LISTEN/NOTIFY. "Anonymous" means genuinely
/// unauthenticated (the AsAnonymous client) — a signed-in reader, like admin and editor, is
/// never served from the cache and always runs its query (the same boundary the read-replica
/// routing uses).
/// </summary>
public class PublicReadCacheTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    [Fact]
    public async Task SecondIdenticalAnonymousRequest_ForEachOfTheSixPublicReads_RunsNoSqlAndReturnsTheSameBody()
    {
        var counter = new SqlCommandCounter();
        await using var factory = CreateFactory(withCacheListener: false, counter);
        var admin = factory.CreateClient().WithRole("Admin");
        var anonymous = factory.CreateClient().AsAnonymous();
        await SeedAsync(admin);

        (string Name, string Url)[] reads =
        [
            ("the post list", "/api/posts?pageSize=50"),
            ("the post by slug", "/api/posts/publicreadcache-post"),
            ("the related posts", "/api/posts/publicreadcache-post/related?count=5"),
            ("the page list", "/api/pages?pageSize=50"),
            ("the page navigation", "/api/pages/navigation"),
            ("the page by slug", "/api/pages/publicreadcache-page"),
        ];

        foreach (var (name, url) in reads)
        {
            counter.Reset();
            var firstResponse = await anonymous.GetAsync(url);
            firstResponse.EnsureSuccessStatusCode();
            var first = await firstResponse.Content.ReadAsStringAsync();
            counter.Commands.Should().BeGreaterThanOrEqualTo(1, $"{name} reads the database on a cache miss");

            counter.Reset();
            var secondResponse = await anonymous.GetAsync(url);
            secondResponse.EnsureSuccessStatusCode();
            var second = await secondResponse.Content.ReadAsStringAsync();
            counter.Commands.Should().Be(0, $"{name} must be answered from the cache on the second identical request");
            second.Should().Be(first, $"{name} returns the same body from the cache");
        }
    }

    [Fact]
    public async Task AdminRequest_AfterCachedAnonymousList_RunsItsQueryAndSeesLiveDrafts()
    {
        var counter = new SqlCommandCounter();
        await using var factory = CreateFactory(withCacheListener: false, counter);
        var admin = factory.CreateClient().WithRole("Admin");
        var anonymous = factory.CreateClient().AsAnonymous();

        await CreatePostAsync(admin, "publicreadcache-admin-published", publish: true);
        var draft = await CreatePostAsync(admin, "publicreadcache-admin-draft", publish: false);

        var publicBody = await anonymous.GetStringAsync("/api/posts?pageSize=50");
        publicBody.Should().NotContain(draft.Slug, "the anonymous list never shows drafts");

        counter.Reset();
        var adminBody = await admin.GetStringAsync("/api/posts?pageSize=50");
        counter.Commands.Should().BeGreaterThanOrEqualTo(1, "an authenticated read is never served from the cache");
        adminBody.Should().Contain(draft.Slug, "the admin read sees live data, including the draft the anonymous list hides");
    }

    [Fact]
    public async Task RenamingThePostThroughOneInstance_UpdatesTheOtherInstancesCachedListAndPost()
    {
        var bCounter = new SqlCommandCounter();
        await using var a = CreateFactory(withCacheListener: false);
        await using var b = CreateFactory(withCacheListener: true, bCounter);
        await WaitForListenerAsync(b);

        var adminA = a.CreateClient().WithRole("Admin");
        var anonymousB = b.CreateClient().AsAnonymous();
        var post = await CreatePostAsync(adminA, "PublicReadCache Title Before", publish: true);

        _ = await anonymousB.GetStringAsync("/api/posts?pageSize=50");
        _ = await anonymousB.GetStringAsync($"/api/posts/{post.Slug}");
        bCounter.Reset();
        _ = await anonymousB.GetStringAsync("/api/posts?pageSize=50");
        _ = await anonymousB.GetStringAsync($"/api/posts/{post.Slug}");
        bCounter.Commands.Should().Be(0, "B's anonymous list and post must be cached before the write");

        var renamed = await UpdatePostAsync(adminA, post, "PublicReadCache Title After");

        await WaitUntilAsync(
            async () =>
            {
                var list = await anonymousB.GetStringAsync("/api/posts?pageSize=50");
                var bySlug = await anonymousB.GetStringAsync($"/api/posts/{post.Slug}");
                return list.Contains(renamed.Title, StringComparison.Ordinal)
                    && bySlug.Contains(renamed.Title, StringComparison.Ordinal);
            },
            TimeSpan.FromSeconds(2),
            "B's cached list and post show the new title after A renames the post");
    }

    [Fact]
    public async Task RenamingTheCategoryThroughOneInstance_UpdatesTheOtherInstancesCachedPost()
    {
        await using var a = CreateFactory(withCacheListener: false);
        await using var b = CreateFactory(withCacheListener: true);
        await WaitForListenerAsync(b);

        var adminA = a.CreateClient().WithRole("Admin");
        var anonymousB = b.CreateClient().AsAnonymous();
        var category = await CreateCategoryAsync(adminA, "PublicReadCache Cat Rename");
        var post = await CreatePostAsync(adminA, "PublicReadCache Category Post", publish: true, categoryId: category.Id);

        var before = await anonymousB.GetStringAsync($"/api/posts/{post.Slug}");
        before.Should().Contain(category.Name, "the cached post carries the category name");

        var renamed = await adminA.PutAsJsonAsync($"/api/categories/{category.Id}",
            new UpdateCategoryRequest("PublicReadCache Cat Renamed", category.Icon));
        renamed.EnsureSuccessStatusCode();

        await WaitUntilAsync(
            async () => (await anonymousB.GetStringAsync($"/api/posts/{post.Slug}"))
                .Contains("PublicReadCache Cat Renamed", StringComparison.Ordinal),
            TimeSpan.FromSeconds(2),
            "B's cached post shows the new category name after A renames the category");
    }

    [Fact]
    public async Task StaffCategoryReads_BypassTheCache_WhileAnonymousReadsKeepUsingIt()
    {
        // B has no listener, so A's write never reaches B's cache: whatever B answers fresh did not come
        // from the cache. The case-only rename keeps the slug, so one route serves both names.
        await using var a = CreateFactory(withCacheListener: false);
        await using var b = CreateFactory(withCacheListener: false);
        var adminA = a.CreateClient().WithRole("Admin");
        var adminB = b.CreateClient().WithRole("Admin");
        var anonymousB = b.CreateClient().AsAnonymous();
        var category = await CreateCategoryAsync(adminA, "Staff Bypass Cat");
        var slug = category.Slug;
        (await anonymousB.GetStringAsync("/api/categories")).Should().Contain("Staff Bypass Cat");
        (await anonymousB.GetStringAsync($"/api/categories/{slug}")).Should().Contain("Staff Bypass Cat");

        (await adminA.PutAsJsonAsync($"/api/categories/{category.Id}", new UpdateCategoryRequest("STAFF BYPASS CAT", category.Icon)))
            .EnsureSuccessStatusCode();

        (await adminB.GetStringAsync("/api/categories")).Should().Contain("STAFF BYPASS CAT", "an Admin list read is never served from the cache");
        (await adminB.GetStringAsync($"/api/categories/{slug}")).Should().Contain("STAFF BYPASS CAT");
        (await anonymousB.GetStringAsync("/api/categories")).Should().Contain("Staff Bypass Cat", "an anonymous read still uses the cache");
        (await anonymousB.GetStringAsync($"/api/categories/{slug}")).Should().Contain("Staff Bypass Cat");
    }

    [Fact]
    public async Task StaffTagReads_BypassTheCache_WhileAnonymousReadsKeepUsingIt()
    {
        await using var a = CreateFactory(withCacheListener: false);
        await using var b = CreateFactory(withCacheListener: false);
        var adminA = a.CreateClient().WithRole("Admin");
        var adminB = b.CreateClient().WithRole("Admin");
        var anonymousB = b.CreateClient().AsAnonymous();
        var tagResponse = await adminA.PostAsJsonAsync("/api/tags", "Staff Bypass Tag");
        tagResponse.EnsureSuccessStatusCode();
        var tag = (await tagResponse.Content.ReadFromJsonAsync<TagDto>())!;
        (await anonymousB.GetStringAsync("/api/tags")).Should().Contain("Staff Bypass Tag");
        (await anonymousB.GetStringAsync($"/api/tags/{tag.Slug}")).Should().Contain("Staff Bypass Tag");

        (await adminA.PutAsJsonAsync($"/api/tags/{tag.Id}", new UpdateTagRequest("STAFF BYPASS TAG", null)))
            .EnsureSuccessStatusCode();

        (await adminB.GetStringAsync("/api/tags")).Should().Contain("STAFF BYPASS TAG", "an Admin list read is never served from the cache");
        (await adminB.GetStringAsync($"/api/tags/{tag.Slug}")).Should().Contain("STAFF BYPASS TAG");
        (await anonymousB.GetStringAsync("/api/tags")).Should().Contain("Staff Bypass Tag", "an anonymous read still uses the cache");
        (await anonymousB.GetStringAsync($"/api/tags/{tag.Slug}")).Should().Contain("Staff Bypass Tag");
    }

    [Fact]
    public async Task SchedulerPublishingThroughOneInstance_GainsThePostInTheOtherInstancesCachedList()
    {
        var bCounter = new SqlCommandCounter();
        await using var a = CreateFactory(withCacheListener: false);
        await using var b = CreateFactory(withCacheListener: true, bCounter);
        await WaitForListenerAsync(b);

        var adminA = a.CreateClient().WithRole("Admin");
        var anonymousB = b.CreateClient().AsAnonymous();
        var category = await CreateCategoryAsync(adminA, "PublicReadCache Sched Cat");

        _ = await anonymousB.GetStringAsync("/api/posts?pageSize=50");
        bCounter.Reset();
        _ = await anonymousB.GetStringAsync("/api/posts?pageSize=50");
        bCounter.Commands.Should().Be(0, "B's list must be cached before the scheduler publishes");

        await InsertScheduledDuePostAsync(category.Id, "publicreadcache-due-post");
        await RunSchedulerOnceAsync(a);

        await WaitUntilAsync(
            async () => (await anonymousB.GetStringAsync("/api/posts?pageSize=50"))
                .Contains("publicreadcache-due-post", StringComparison.Ordinal),
            TimeSpan.FromSeconds(2),
            "B's cached list gains the due post after A's scheduler publishes it");
    }

    [Fact]
    public async Task UpdatingPageNavigationThroughOneInstance_UpdatesTheOtherInstancesCachedNavigation()
    {
        await using var a = CreateFactory(withCacheListener: false);
        await using var b = CreateFactory(withCacheListener: true);
        await WaitForListenerAsync(b);

        var adminA = a.CreateClient().WithRole("Admin");
        var anonymousB = b.CreateClient().AsAnonymous();
        var page = await CreatePageAsync(adminA, "PublicReadCache Nav Page");

        var before = await anonymousB.GetStringAsync("/api/pages/navigation");
        before.Should().Contain(page.Slug, "the cached navigation lists the page");

        var updated = await adminA.PutAsJsonAsync($"/api/posts/{page.Id}",
            new UpdatePostRequest(page.Title, page.MarkdownContent, Kind: PostKind.Page, NavigationPlacement: NavigationPlacement.None));
        updated.EnsureSuccessStatusCode();

        await WaitUntilAsync(
            async () => !(await anonymousB.GetStringAsync("/api/pages/navigation"))
                .Contains(page.Slug, StringComparison.Ordinal),
            TimeSpan.FromSeconds(30),
            "B's cached navigation drops the page after A clears its navigation flag");
    }

    [Fact]
    public async Task MovingAPageToTheFooterThroughOneInstance_UpdatesTheOtherInstancesCachedFooterAndSidebar()
    {
        await using var a = CreateFactory(withCacheListener: false);
        await using var b = CreateFactory(withCacheListener: true);
        await WaitForListenerAsync(b);

        var adminA = a.CreateClient().WithRole("Admin");
        var anonymousB = b.CreateClient().AsAnonymous();
        var page = await CreatePageAsync(adminA, "PublicReadCache Footer Page");

        (await anonymousB.GetStringAsync("/api/pages/navigation?placement=Footer"))
            .Should().NotContain(page.Slug, "the page starts in the sidebar");
        (await anonymousB.GetStringAsync("/api/pages/navigation"))
            .Should().Contain(page.Slug);

        var updated = await adminA.PutAsJsonAsync($"/api/posts/{page.Id}",
            new UpdatePostRequest(page.Title, page.MarkdownContent, Kind: PostKind.Page, NavigationPlacement: NavigationPlacement.Footer));
        updated.EnsureSuccessStatusCode();

        await WaitUntilAsync(
            async () => (await anonymousB.GetStringAsync("/api/pages/navigation?placement=Footer"))
                .Contains(page.Slug, StringComparison.Ordinal)
                && !(await anonymousB.GetStringAsync("/api/pages/navigation"))
                    .Contains(page.Slug, StringComparison.Ordinal),
            TimeSpan.FromSeconds(2),
            "B's cached lists move the page from the sidebar to the footer after A saves it");
    }

    [Fact]
    public async Task PageViewThroughOneInstance_DoesNotClearTheOtherInstancesCache()
    {
        var bCounter = new SqlCommandCounter();
        await using var a = CreateFactory(withCacheListener: false);
        await using var b = CreateFactory(withCacheListener: true, bCounter);
        await WaitForListenerAsync(b);

        var adminA = a.CreateClient().WithRole("Admin");
        var anonymousA = a.CreateClient().AsAnonymous();
        var anonymousB = b.CreateClient().AsAnonymous();
        var post = await CreatePostAsync(adminA, "PublicReadCache PageView", publish: true);

        _ = await anonymousB.GetStringAsync("/api/posts?pageSize=50");
        bCounter.Reset();
        _ = await anonymousB.GetStringAsync("/api/posts?pageSize=50");
        bCounter.Commands.Should().Be(0, "B's list must be cached before the page view");

        var view = await anonymousA.PostAsJsonAsync("/api/analytics/pageview", new PageViewRequest(post.Id));
        view.EnsureSuccessStatusCode();

        // A page view must never invalidate: keep reading the cached list for a bounded window
        // on the concrete condition "a read hit the database" (which a cleared cache would do)
        // and require that never happens.
        var cleared = await WaitForCacheMissAsync(anonymousB, bCounter, TimeSpan.FromSeconds(2));
        cleared.Should().BeFalse("a page view does not clear B's cache");
    }

    [Fact]
    public async Task UpdatingBannerAltTextThroughOneInstance_UpdatesTheOtherInstancesCachedPost()
    {
        await using var a = CreateFactory(withCacheListener: false);
        await using var b = CreateFactory(withCacheListener: true);
        await WaitForListenerAsync(b);

        var adminA = a.CreateClient().WithRole("Admin");
        var anonymousB = b.CreateClient().AsAnonymous();
        var bannerId = await SeedBannerAsync(a, "publicreadcache-banner.png", "alt before");
        var created = await adminA.PostAsJsonAsync("/api/posts",
            new CreatePostRequest("PublicReadCache Banner Post", "## A short public-read-cache body", BannerImageId: bannerId));
        created.EnsureSuccessStatusCode();
        var post = await PublishAsync(adminA, (await created.Content.ReadFromJsonAsync<PostDto>())!);

        var before = await anonymousB.GetStringAsync($"/api/posts/{post.Slug}");
        before.Should().Contain("alt before", "the cached post carries the banner alt text");

        var updated = await adminA.PutAsJsonAsync($"/api/media/{bannerId}", new UpdateMediaRequest("alt after"));
        updated.EnsureSuccessStatusCode();

        await WaitUntilAsync(
            async () => (await anonymousB.GetStringAsync($"/api/posts/{post.Slug}"))
                .Contains("alt after", StringComparison.Ordinal),
            TimeSpan.FromSeconds(2),
            "B's cached post shows the new alt text after A updates the media asset");
    }

    [Fact]
    public async Task BackfillingPostSummaries_InvalidatesTheCachedList()
    {
        var counter = new SqlCommandCounter();
        await using var factory = CreateFactory(withCacheListener: false, counter);
        var admin = factory.CreateClient().WithRole("Admin");
        var anonymous = factory.CreateClient().AsAnonymous();

        await InsertPostWithNullSummaryAsync("publicreadcache-backfill-target");

        var before = await anonymous.GetStringAsync("/api/posts?pageSize=50");
        before.Should().NotContain("A short backfill body",
            "the seeded post's summary is still empty before the backfill runs");

        await RunBackfillOnceAsync(factory);

        counter.Reset();
        var after = await anonymous.GetStringAsync("/api/posts?pageSize=50");
        counter.Commands.Should().BeGreaterThanOrEqualTo(1,
            "the backfill is a content write, so it must invalidate the cached list");
        after.Should().Contain("A short backfill body",
            "the refreshed list shows the backfilled summary");
    }

    [Fact]
    public async Task RandomNotFoundReads_NeverTakeMoreThanTheNegativeBudget()
    {
        // Brief 13c item 3b changed the rule this test pinned ("not-found results are never cached"): a
        // not-found is now cached for 30 s, so repeated misses do not each hit the database — but within a
        // fixed budget, so random missing slugs still cannot fill the cache and push out the real entries.
        await using var factory = CreateFactory(withCacheListener: false);
        var anonymous = factory.CreateClient().AsAnonymous();
        var cache = (MemoryPublicReadCache)factory.Services.GetRequiredService<IPublicReadCache>();

        for (var attempt = 1; attempt <= MemoryPublicReadCache.DefaultMaxNegativeEntries + 50; attempt++)
        {
            var missing = await anonymous.GetAsync($"/api/posts/not-a-real-slug-{Guid.NewGuid():N}");
            missing.StatusCode.Should().Be(HttpStatusCode.NotFound, "the slug does not exist");
        }

        cache.Count.Should().BeLessThanOrEqualTo(MemoryPublicReadCache.DefaultMaxNegativeEntries,
            "random missing slugs must not fill the cache: not-found results share a small budget");
    }

    [Fact]
    public async Task InvalidatedDuringRefillWindow_TheRefillIsNotStored()
    {
        var cache = new MemoryPublicReadCache(new MemoryCache(new MemoryCacheOptions()));
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cache.BeforeStore = async () =>
        {
            reached.TrySetResult();
            await resume.Task;
        };

        var calls = 0;
        Task<string> Factory(CancellationToken _) =>
            Task.FromResult(Interlocked.Increment(ref calls) == 1 ? "value-1" : "value-2");

        var read = cache.GetOrCreateAsync(CacheGroup.Posts, "posts:refill-window", Factory);

        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        // The invalidation lands while the refill sits between its query and its store ...
        await cache.InvalidateAsync(CacheGroup.Posts);
        // ... and only then does the refill complete its store.
        resume.TrySetResult();

        (await read).Should().Be("value-1", "the caller still gets the value it fetched");

        var second = await cache.GetOrCreateAsync(CacheGroup.Posts, "posts:refill-window", Factory);
        second.Should().Be("value-2",
            "an invalidation during the refill window must leave nothing cached");
    }

    [Fact]
    public async Task BurstOfNotifications_AreAllApplied()
    {
        await using var b = CreateFactory(withCacheListener: true);
        await WaitForListenerAsync(b);
        var admin = b.CreateClient().WithRole("Admin");
        var anonymous = b.CreateClient().AsAnonymous();
        await SeedAsync(admin);
        var bCache = (MemoryPublicReadCache)b.Services.GetRequiredService<IPublicReadCache>();

        // One tracked entry per cache group.
        _ = await anonymous.GetStringAsync("/api/categories");
        _ = await anonymous.GetStringAsync("/api/tags");
        _ = await anonymous.GetStringAsync("/api/posts?pageSize=44");
        _ = await anonymous.GetStringAsync("/api/pages/navigation");
        bCache.Count.Should().Be(4, "one cached entry per group before the burst");

        // A burst of four notifications, one per group, sent back to back.
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        foreach (var payload in new[] { "categories", "tags", "posts", "pages" })
        {
            await using var notify = connection.CreateCommand();
            notify.CommandText = $"SELECT pg_notify('croft_cache', '{payload}')";
            await notify.ExecuteNonQueryAsync();
        }

        await WaitUntilAsync(
            () => Task.FromResult(bCache.Count == 0),
            TimeSpan.FromSeconds(5),
            "every notification in the burst is applied: each clears its own group's entry");
    }

    [Fact]
    public async Task HeldReadStraddlingALocalWrite_DoesNotPoisonTheCacheWithTheOldValue()
    {
        var gate = new HeldResultInterceptor("p.\"Slug\"");
        await using var factory = CreateFactory(withCacheListener: false, gate);
        var admin = factory.CreateClient().WithRole("Admin");
        var anonymous = factory.CreateClient().AsAnonymous();
        var category = await CreateCategoryAsync(admin, "Straddle Local Cat");
        var post = await CreatePostAsync(admin, "Straddle Local Post", publish: true, categoryId: category.Id);

        // The read's factory runs its query (snapshot: the old category name) and is held
        // before it can complete — the window a write's invalidation can race.
        gate.Arm();
        var heldRead = anonymous.GetAsync($"/api/posts/{post.Slug}");
        await gate.Held.WaitAsync(TimeSpan.FromSeconds(30));

        var renamed = await admin.PutAsJsonAsync($"/api/categories/{category.Id}",
            new UpdateCategoryRequest("Straddle Local Cat Renamed", category.Icon));
        renamed.EnsureSuccessStatusCode();

        gate.Release();
        var heldResponse = await heldRead;
        heldResponse.EnsureSuccessStatusCode();

        var commandsBefore = gate.Commands;
        var secondResponse = await anonymous.GetAsync($"/api/posts/{post.Slug}");
        secondResponse.EnsureSuccessStatusCode();
        var second = await secondResponse.Content.ReadAsStringAsync();

        gate.Commands.Should().BeGreaterThan(commandsBefore,
            "a read that straddled the write must not leave the pre-write value in the cache: the next read queries again");
        second.Should().Contain("Straddle Local Cat Renamed",
            "the next read sees what the write committed");
    }

    [Fact]
    public async Task HeldReadStraddlingARemoteWrite_DoesNotPoisonTheCacheWithTheOldValue()
    {
        var bGate = new HeldResultInterceptor("p.\"Slug\"");
        await using var a = CreateFactory(withCacheListener: false);
        await using var b = CreateFactory(withCacheListener: true, bGate);
        await WaitForListenerAsync(b);

        var adminA = a.CreateClient().WithRole("Admin");
        var anonymousB = b.CreateClient().AsAnonymous();
        var category = await CreateCategoryAsync(adminA, "Straddle Remote Cat");
        var post = await CreatePostAsync(adminA, "Straddle Remote Post", publish: true, categoryId: category.Id);
        var bCache = (MemoryPublicReadCache)b.Services.GetRequiredService<IPublicReadCache>();

        // Prime B with a tracked entry the notification will drop: it is how the test sees
        // that B's listener received the write's invalidation.
        _ = await anonymousB.GetStringAsync("/api/categories");
        bCache.Count.Should().BeGreaterThanOrEqualTo(1, "B holds the primed categories entry");

        bGate.Arm();
        var heldRead = anonymousB.GetAsync($"/api/posts/{post.Slug}");
        await bGate.Held.WaitAsync(TimeSpan.FromSeconds(30));

        var renamed = await adminA.PutAsJsonAsync($"/api/categories/{category.Id}",
            new UpdateCategoryRequest("Straddle Remote Cat Renamed", category.Icon));
        renamed.EnsureSuccessStatusCode();

        // Release only once B received the notification (its listener dropped the primed entry).
        await WaitUntilAsync(
            () => Task.FromResult(bCache.Count == 0),
            TimeSpan.FromSeconds(5),
            "B's cache listener receives A's write notification");

        bGate.Release();
        var heldResponse = await heldRead;
        heldResponse.EnsureSuccessStatusCode();

        var commandsBefore = bGate.Commands;
        var secondResponse = await anonymousB.GetAsync($"/api/posts/{post.Slug}");
        secondResponse.EnsureSuccessStatusCode();
        var second = await secondResponse.Content.ReadAsStringAsync();

        bGate.Commands.Should().BeGreaterThan(commandsBefore,
            "a read that straddled the write must not leave the pre-write value in B's cache: the next read queries again");
        second.Should().Contain("Straddle Remote Cat Renamed",
            "B serves what A's write committed");
    }

    [Fact]
    public async Task ConcurrentRefillsOfOneKey_KeepItInvalidatableByTheNextContentWrite()
    {
        var gate = new HoldingCommandInterceptor(holdCount: 1);
        await using var factory = CreateFactory(withCacheListener: false, gate);
        var admin = factory.CreateClient().WithRole("Admin");
        var anonymous = factory.CreateClient().AsAnonymous();
        var post = await CreatePostAsync(admin, "PublicReadCache Refill", publish: true);
        const string url = "/api/posts?pageSize=50";

        // Three concurrent cache misses on one key, the post-invalidation stampede of the hot home
        // key: since brief 13c they share one refill, so one command reaches the gate (it was three
        // before) and the other two readers wait for that refill's result.
        gate.Arm();
        var refills = Enumerable.Range(0, 3).Select(_ => anonymous.GetAsync(url)).ToList();
        await gate.Held.WaitAsync(TimeSpan.FromSeconds(30));
        gate.Release();
        var responses = await Task.WhenAll(refills);
        responses.Should().OnlyContain(response => response.IsSuccessStatusCode);

        var arrivals = gate.Arrived;
        _ = await anonymous.GetStringAsync(url);
        gate.Arrived.Should().Be(arrivals, "the key is cached after the concurrent refills");

        var renamed = await UpdatePostAsync(admin, post, "PublicReadCache Refill Renamed");

        await WaitUntilAsync(
            async () => (await anonymous.GetStringAsync(url))
                .Contains(renamed.Title, StringComparison.Ordinal),
            TimeSpan.FromSeconds(2),
            "the refilled key is still invalidated by the next content write");
    }

    [Fact]
    public async Task TwoThousandOneDistinctAnonymousReads_LeaveAtMost2000CacheEntries()
    {
        await using var factory = CreateFactory(withCacheListener: false);
        var admin = factory.CreateClient().WithRole("Admin");
        var anonymous = factory.CreateClient().AsAnonymous();
        await SeedAsync(admin);
        var cache = (MemoryPublicReadCache)factory.Services.GetRequiredService<IPublicReadCache>();

        for (var page = 1; page <= 2001; page++)
        {
            var response = await anonymous.GetAsync($"/api/posts?page={page}&pageSize=1");
            response.IsSuccessStatusCode.Should().BeTrue("every distinct read is served");
        }

        // WORKAROUND(W-079, .NET): IMemoryCache eviction bookkeeping lands on the thread pool, so the test waits on the count
        // Eviction bookkeeping lands on the thread pool; wait for it on the concrete count
        // before asserting the bound.
        await WaitUntilAsync(
            () => Task.FromResult(cache.Count <= 2000),
            TimeSpan.FromSeconds(2),
            "the cache settles at no more than 2000 tracked entries");
        // END WORKAROUND(W-079)
        cache.Count.Should().BeLessThanOrEqualTo(2000,
            "the cache is bounded: SizeLimit 2000 entries with Size 1 each");
    }

    private static async Task SeedAsync(HttpClient admin)
    {
        var category = await CreateCategoryAsync(admin, "PublicReadCache Cat");
        await CreatePostAsync(admin, "PublicReadCache Post", publish: true,
            categoryId: category.Id, tags: ["publicreadcache-tag"]);
        await CreatePageAsync(admin, "PublicReadCache Page");
    }

    private static async Task<CategoryDto> CreateCategoryAsync(HttpClient admin, string name)
    {
        var response = await admin.PostAsJsonAsync("/api/categories", name);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CategoryDto>())!;
    }

    private static async Task<PostDto> CreatePostAsync(
        HttpClient admin, string title, bool publish, Guid? categoryId = null, IReadOnlyList<string>? tags = null)
    {
        var created = await admin.PostAsJsonAsync("/api/posts",
            new CreatePostRequest(title, "## A short public-read-cache body", Tags: tags, CategoryId: categoryId));
        created.EnsureSuccessStatusCode();
        var post = (await created.Content.ReadFromJsonAsync<PostDto>())!;
        return publish ? await PublishAsync(admin, post) : post;
    }

    private static async Task<PostDto> CreatePageAsync(HttpClient admin, string title)
    {
        var created = await admin.PostAsJsonAsync("/api/posts",
            new CreatePostRequest(title, "## A short public-read-cache page", Kind: PostKind.Page, NavigationPlacement: NavigationPlacement.Sidebar));
        created.EnsureSuccessStatusCode();
        var page = (await created.Content.ReadFromJsonAsync<PostDto>())!;
        return await PublishAsync(admin, page);
    }

    private static async Task<PostDto> PublishAsync(HttpClient admin, PostDto post)
    {
        var published = await admin.PostAsJsonAsync($"/api/posts/{post.Id}/publish",
            new PublishRequest(DateTime.UtcNow.AddMinutes(-1)));
        published.EnsureSuccessStatusCode();
        return (await published.Content.ReadFromJsonAsync<PostDto>())!;
    }

    private static async Task<PostDto> UpdatePostAsync(HttpClient admin, PostDto post, string title)
    {
        var response = await admin.PutAsJsonAsync($"/api/posts/{post.Id}", new UpdatePostRequest(title, post.MarkdownContent));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PostDto>())!;
    }

    private static async Task<Guid> SeedBannerAsync(BlogApiFactory factory, string fileName, string altText)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        var asset = new MediaAsset
        {
            Id = Guid.NewGuid(),
            FileName = fileName,
            OriginalName = fileName,
            ContentType = "image/png",
            FilePath = fileName,
            UploadedAt = DateTime.UtcNow,
            UploadedBy = "test-user-id",
            AltText = altText,
            Scope = MediaAssetScope.Global
        };
        db.MediaAssets.Add(asset);
        await db.SaveChangesAsync();
        return asset.Id;
    }

    private async Task InsertScheduledDuePostAsync(Guid categoryId, string slug) =>
        await InsertPostAsync(slug, PostStatus.Scheduled, DateTime.UtcNow.AddMinutes(-5), categoryId);

    private async Task InsertPostWithNullSummaryAsync(string slug) =>
        await InsertPostAsync(slug, PostStatus.Published, DateTime.UtcNow.AddHours(-1), categoryId: null);

    /// <summary>Writes a post row directly: Summary stays null (only the backfill fills it).</summary>
    private async Task InsertPostAsync(string slug, PostStatus status, DateTime publishedAt, Guid? categoryId)
    {
        await using var db = new BlogDbContext(
            new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(fixture.ConnectionString).Options);
        db.Posts.Add(new Post
        {
            Slug = slug,
            Title = slug,
            MarkdownContent = "A short backfill body that the summary extractor picks up as the first paragraph.",
            Status = status,
            Kind = PostKind.Post,
            CategoryId = categoryId,
            PublishedAt = publishedAt,
            CreatedAt = publishedAt,
            UpdatedAt = publishedAt,
            ContentEditedAt = publishedAt,
            AuthorId = "publicreadcache-author",
            AuthorName = "Public Read Cache"
        });
        await db.SaveChangesAsync();
    }

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

    private static async Task RunBackfillOnceAsync(BlogApiFactory factory)
    {
        var backfill = new PostSummaryBackfillService(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PostSummaryBackfillService>.Instance,
            factory.Services.GetRequiredService<ICacheNotifier>());

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        var result = await backfill.RunOnceAsync(db);
        result.Filled.Should().BeGreaterThanOrEqualTo(1, "the seeded post still needs its summary");
    }

    private async Task WaitForListenerAsync(BlogApiFactory factory)
    {
        var listener = factory.Services.GetRequiredService<CacheListenerService>();
        var samples = new List<string>();
        var nextSample = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        try
        {
            await WaitUntilAsync(
                async () =>
                {
                    await Task.Yield();
                    if (listener.BackendPid is null && DateTime.UtcNow >= nextSample)
                    {
                        // While it is still not connected: what the server says about it, every 3 s.
                        nextSample = DateTime.UtcNow + TimeSpan.FromSeconds(3);
                        samples.Add($"{DateTime.UtcNow:HH:mm:ss.fff} {await CacheListenerDiagnostics.SampleServerAsync(fixture.ConnectionString)}");
                    }

                    return listener.BackendPid is not null;
                },
                TimeSpan.FromSeconds(30),
                "B's cache listener connects and finishes its setup");
        }
        catch (Xunit.Sdk.XunitException ex)
        {
            throw new Xunit.Sdk.XunitException(
                $"{ex.Message}{Environment.NewLine}{CacheListenerDiagnostics.Describe(factory)}"
                + $"{Environment.NewLine}Server samples while waiting:{Environment.NewLine}  {string.Join($"{Environment.NewLine}  ", samples)}", ex);
        }
    }

    /// <summary>
    /// Reads the same cached list until a read reaches the database (which a cleared cache
    /// would do) or the window ends; true means the cache was cleared.
    /// </summary>
    private static async Task<bool> WaitForCacheMissAsync(HttpClient anonymous, SqlCommandCounter counter, TimeSpan window)
    {
        var deadline = DateTime.UtcNow + window;
        while (DateTime.UtcNow < deadline)
        {
            _ = await anonymous.GetStringAsync("/api/posts?pageSize=50");
            if (counter.Commands > 0)
                return true;
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        return counter.Commands > 0;
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

    private BlogApiFactory CreateFactory(bool withCacheListener, DbCommandInterceptor? interceptor = null)
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);

        return new BlogApiFactory(
            fixture.ConnectionString,
            storagePath,
            configureServices: services =>
            {
                // The scheduler and backfill would run their own SQL on their own schedule;
                // these tests drive them explicitly where the brief calls for it. The cache
                // listener is the one hosted service that must run.
                services.RemoveAll<IHostedService>();
                if (withCacheListener)
                {
                    services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<CacheListenerService>());
                    CacheListenerDiagnostics.Collect(services);
                }
            },
            commandInterceptor: interceptor);
    }
}
