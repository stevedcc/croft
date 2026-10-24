using Croft.Api.Services;
using Croft.Api.Tests.Fixtures;
using Croft.Shared.Contracts;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System.Net;

namespace Croft.Api.Tests;

/// <summary>
/// Brief 13c item 3b (.NET review I-6 and M-2): the user-controlled parts of a public cache key are
/// clamped so one crawler cannot evict the hot set, concurrent misses for one key share one refill, and a
/// not-found or empty result is cached briefly.
/// </summary>
public class PublicReadCacheKeyTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    // ---- the cache itself --------------------------------------------------------------------

    [Fact]
    public async Task FiftyConcurrentMissesForOneKey_ShareOneRefill()
    {
        var cache = new MemoryPublicReadCache(new MemoryCache(new MemoryCacheOptions()));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        async Task<string> Factory(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await release.Task;
            return "value";
        }

        var first = cache.GetOrCreateAsync(CacheGroup.Posts, "posts:stampede", Factory);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var others = Enumerable.Range(0, 49).Select(_ => cache.GetOrCreateAsync(CacheGroup.Posts, "posts:stampede", Factory)).ToList();
        release.SetResult();

        var results = await Task.WhenAll([first, .. others]);

        results.Should().OnlyContain(value => value == "value");
        calls.Should().Be(1, "50 concurrent misses for one key cause one refill, not 50 database queries");
    }

    [Fact]
    public async Task AMissThatStartsAfterAnInvalidation_DoesNotShareARefillThatBeganBeforeIt()
    {
        var cache = new MemoryPublicReadCache(new MemoryCache(new MemoryCacheOptions()));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        async Task<string> Factory(CancellationToken _)
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 1)
            {
                started.TrySetResult();
                await release.Task;
                return "before-the-write";
            }

            return "after-the-write";
        }

        var old = cache.GetOrCreateAsync(CacheGroup.Posts, "posts:shared-old", Factory);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await cache.InvalidateAsync(CacheGroup.Posts);

        var fresh = await cache.GetOrCreateAsync(CacheGroup.Posts, "posts:shared-old", Factory);
        release.SetResult();
        await old;

        fresh.Should().Be("after-the-write", "a read that began after the write must not take the result of one that began before it");
        (await cache.GetOrCreateAsync(CacheGroup.Posts, "posts:shared-old", Factory)).Should().Be("after-the-write",
            "and the older refill is not what stays cached");
    }

    [Fact]
    public async Task AFollowerWhoseLeaderWasCancelled_RunsTheRefillItself()
    {
        var cache = new MemoryPublicReadCache(new MemoryCache(new MemoryCacheOptions()));
        using var cancelLeader = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        async Task<string> Factory(CancellationToken token)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }

            return "value";
        }

        var leader = cache.GetOrCreateAsync(CacheGroup.Posts, "posts:cancelled-leader", Factory, cancelLeader.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var follower = cache.GetOrCreateAsync(CacheGroup.Posts, "posts:cancelled-leader", Factory);
        await cancelLeader.CancelAsync();

        await leader.Invoking(task => task).Should().ThrowAsync<OperationCanceledException>();
        (await follower).Should().Be("value", "one reader leaving must not fail the readers waiting on the same refill");
    }

    [Fact]
    public async Task ANullResult_IsCachedBriefly_ThenRefilled()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = new MemoryPublicReadCache(new MemoryCache(new MemoryCacheOptions()), time);
        var calls = 0;
        Task<string?> Factory(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<string?>(null);
        }

        (await cache.GetOrCreateAsync(CacheGroup.Posts, "posts:slug:gone", Factory)).Should().BeNull();
        (await cache.GetOrCreateAsync(CacheGroup.Posts, "posts:slug:gone", Factory)).Should().BeNull();
        calls.Should().Be(1, "a not-found is served from the cache within its short lifetime");

        time.Advance(MemoryPublicReadCache.NegativeExpiry + TimeSpan.FromSeconds(1));
        (await cache.GetOrCreateAsync(CacheGroup.Posts, "posts:slug:gone", Factory)).Should().BeNull();
        calls.Should().Be(2, "after its lifetime (30 s) the next read asks the database again");
    }

    [Fact]
    public async Task AnEmptyResult_IsCachedBriefly_LikeANotFound()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = new MemoryPublicReadCache(new MemoryCache(new MemoryCacheOptions()), time);
        var calls = 0;
        Task<List<string>> Factory(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new List<string>());
        }

        await cache.GetOrCreateAsync(CacheGroup.Posts, "posts:list:empty", Factory, isEmpty: list => list.Count == 0);
        await cache.GetOrCreateAsync(CacheGroup.Posts, "posts:list:empty", Factory, isEmpty: list => list.Count == 0);
        calls.Should().Be(1);

        time.Advance(MemoryPublicReadCache.NegativeExpiry + TimeSpan.FromSeconds(1));
        await cache.GetOrCreateAsync(CacheGroup.Posts, "posts:list:empty", Factory, isEmpty: list => list.Count == 0);
        calls.Should().Be(2);
    }

    [Fact]
    public async Task ANotFound_IsDroppedByTheInvalidationOfItsGroup()
    {
        var cache = new MemoryPublicReadCache(new MemoryCache(new MemoryCacheOptions()));
        var calls = 0;
        Task<string?> Factory(CancellationToken _) =>
            Task.FromResult(Interlocked.Increment(ref calls) == 1 ? null : "created");

        (await cache.GetOrCreateAsync(CacheGroup.Posts, "posts:slug:soon", Factory)).Should().BeNull();
        await cache.InvalidateAsync(CacheGroup.Posts);

        (await cache.GetOrCreateAsync(CacheGroup.Posts, "posts:slug:soon", Factory)).Should().Be("created",
            "creating the post must make the cached 404 vanish at once");
    }

    [Fact]
    public async Task ManyDistinctNotFounds_TakeNoMoreThanTheNegativeBudget()
    {
        var cache = new MemoryPublicReadCache(new MemoryCache(new MemoryCacheOptions { SizeLimit = 2000 }));

        for (var i = 0; i < 500; i++)
            await cache.GetOrCreateAsync<string?>(CacheGroup.Posts, $"posts:slug:random-{i}", _ => Task.FromResult<string?>(null));

        cache.Count.Should().BeLessThanOrEqualTo(MemoryPublicReadCache.DefaultMaxNegativeEntries,
            "a crawler asking for random slugs must not evict the hot set");
    }

    [Fact]
    public async Task AnExpiredNotFound_ReplacedByARefill_StaysInvalidatable()
    {
        // W-079: EvictionReason.Replaced fires after the newer Set owns the key; the callback must not
        // drop the newer entry's tracking, or the next content write would miss it and serve it past the write.
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = new MemoryPublicReadCache(new MemoryCache(new MemoryCacheOptions()), time);
        var calls = 0;
        Task<string?> Factory(CancellationToken _) =>
            Task.FromResult<string?>(Interlocked.Increment(ref calls) == 1 ? null : $"value-{calls}");

        (await cache.GetOrCreateAsync(CacheGroup.Posts, "posts:slug:replaced", Factory)).Should().BeNull();
        time.Advance(MemoryPublicReadCache.NegativeExpiry + TimeSpan.FromSeconds(1));
        (await cache.GetOrCreateAsync(CacheGroup.Posts, "posts:slug:replaced", Factory)).Should().Be("value-2");

        // The eviction bookkeeping of the replaced entry lands on the thread pool: wait for it on the count it settles.
        await WaitUntilAsync(() => cache.NegativeCount == 0, "the replaced not-found's eviction callback has run");

        await cache.InvalidateAsync(CacheGroup.Posts);
        (await cache.GetOrCreateAsync(CacheGroup.Posts, "posts:slug:replaced", Factory)).Should().Be("value-3",
            "the write dropped the entry that replaced the not-found");
    }

    [Fact]
    public async Task TheCache_NeverHoldsMoreThanItsSizeLimit()
    {
        var cache = new MemoryPublicReadCache(new MemoryCache(new MemoryCacheOptions { SizeLimit = 2000 }));

        for (var i = 0; i < 2001; i++)
            await cache.GetOrCreateAsync(CacheGroup.Posts, $"posts:list:{i}", _ => Task.FromResult("body"));

        await WaitUntilAsync(() => cache.Count <= 2000, "the cache settles at no more than 2000 entries");
    }

    // ---- the keys, through the API -----------------------------------------------------------

    [Fact]
    public async Task PageSizes_AboveTheMaximum_ShareOneEntryWithTheMaximum()
    {
        var counter = new SqlCommandCounter();
        await using var factory = CreateFactory(counter);
        var anonymous = await SeededAnonymousAsync(factory);
        var cache = (MemoryPublicReadCache)factory.Services.GetRequiredService<IPublicReadCache>();

        (await anonymous.GetAsync("/api/posts?pageSize=10000")).EnsureSuccessStatusCode();
        var entries = cache.Count;
        counter.Reset();
        var atMax = await anonymous.GetFromJsonAsync<PagedResult<PostSummaryDto>>("/api/posts?pageSize=100");
        var above = await anonymous.GetFromJsonAsync<PagedResult<PostSummaryDto>>("/api/posts?pageSize=101");

        counter.Commands.Should().Be(0, "page sizes 10000, 101 and 100 are one key: the query clamps them to 100 as well");
        cache.Count.Should().Be(entries, "no new entry per page size");
        atMax!.PageSize.Should().Be(100);
        above.Should().BeEquivalentTo(atMax);
    }

    [Fact]
    public async Task APageBeyondTheMaximum_AnswersAnEmptyPage_WithoutQueryingOrCaching()
    {
        var counter = new SqlCommandCounter();
        await using var factory = CreateFactory(counter);
        var anonymous = await SeededAnonymousAsync(factory);
        var cache = (MemoryPublicReadCache)factory.Services.GetRequiredService<IPublicReadCache>();
        var entries = cache.Count;
        counter.Reset();

        var response = await anonymous.GetAsync("/api/posts?page=999999&pageSize=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<PagedResult<PostSummaryDto>>())!.Items.Should().BeEmpty();
        cache.Count.Should().Be(entries, "page 999999 must not create an entry");
        counter.Commands.Should().Be(0, "and needs no query: it is past any page the site serves");
    }

    [Fact]
    public async Task SearchTerms_AreNormalisedBeforeTheyBecomeAKey()
    {
        var counter = new SqlCommandCounter();
        await using var factory = CreateFactory(counter);
        var anonymous = await SeededAnonymousAsync(factory);

        (await anonymous.GetAsync("/api/posts?searchTerm=Cache+Key+Seed")).EnsureSuccessStatusCode();
        counter.Reset();
        (await anonymous.GetAsync("/api/posts?searchTerm=%20%20cache%20%20KEY%09seed%20")).EnsureSuccessStatusCode();

        counter.Commands.Should().Be(0, "a term trimmed, whitespace-collapsed and lower-cased is the same key");
    }

    [Fact]
    public async Task ALongSearchTerm_IsCappedForTheKeyAndTheQueryAlike()
    {
        // A post whose body holds exactly the capped word. A term that goes beyond the cap only finds it
        // if the QUERY used the capped text too (an uncapped full-text query for the longer word matches
        // nothing), so the results, not just the cache key, show the cap.
        var counter = new SqlCommandCounter();
        await using var factory = CreateFactory(counter);
        var admin = factory.CreateClient().WithRole("Admin");
        var word = new string('a', MaxSearchTermLength);
        var created = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest("Capped Term Post", $"## Body {word} end"));
        created.EnsureSuccessStatusCode();
        var post = (await created.Content.ReadFromJsonAsync<PostDto>())!;
        (await admin.PostAsJsonAsync($"/api/posts/{post.Id}/publish", new PublishRequest(DateTime.UtcNow.AddMinutes(-1)))).EnsureSuccessStatusCode();
        var anonymous = factory.CreateClient().AsAnonymous();
        var cache = (MemoryPublicReadCache)factory.Services.GetRequiredService<IPublicReadCache>();

        var first = await anonymous.GetFromJsonAsync<PagedResult<PostSummaryDto>>($"/api/posts?searchTerm={word}XXXX");
        first!.Items.Should().ContainSingle(item => item.Id == post.Id, "the query used the capped term, which the post contains");
        var entries = cache.Count;
        counter.Reset();
        var second = await anonymous.GetFromJsonAsync<PagedResult<PostSummaryDto>>($"/api/posts?searchTerm={word}YYYYYYYY");

        second.Should().BeEquivalentTo(first, "terms that differ only beyond the cap are one query");
        counter.Commands.Should().Be(0, "and one cache key");
        cache.Count.Should().Be(entries);
    }

    [Fact]
    public async Task SearchResultsBeyondTheFirstPages_AreNotCached()
    {
        var counter = new SqlCommandCounter();
        await using var factory = CreateFactory(counter);
        var anonymous = await SeededAnonymousAsync(factory);
        var cache = (MemoryPublicReadCache)factory.Services.GetRequiredService<IPublicReadCache>();

        (await anonymous.GetAsync("/api/posts?searchTerm=cache&page=4")).EnsureSuccessStatusCode();
        var entries = cache.Count;
        counter.Reset();
        (await anonymous.GetAsync("/api/posts?searchTerm=cache&page=4")).EnsureSuccessStatusCode();

        counter.Commands.Should().BeGreaterThanOrEqualTo(1, "a deep search page is queried every time");
        cache.Count.Should().Be(entries, "and never takes an entry");
    }

    [Fact]
    public async Task RelatedCounts_AboveTheMaximum_ShareOneEntry()
    {
        var counter = new SqlCommandCounter();
        await using var factory = CreateFactory(counter);
        var anonymous = await SeededAnonymousAsync(factory);

        (await anonymous.GetAsync("/api/posts/cache-key-seed/related?count=10")).EnsureSuccessStatusCode();
        counter.Reset();
        (await anonymous.GetAsync("/api/posts/cache-key-seed/related?count=500")).EnsureSuccessStatusCode();

        counter.Commands.Should().Be(0, "the loader clamps the count to 10, so the key does too");
    }

    [Fact]
    public async Task ANotFoundPost_IsServedFromTheCacheBriefly_AndAppearsOnceCreated()
    {
        var counter = new SqlCommandCounter();
        await using var factory = CreateFactory(counter);
        var admin = factory.CreateClient().WithRole("Admin");
        var anonymous = factory.CreateClient().AsAnonymous();
        const string Slug = "cache-key-not-yet-written";

        (await anonymous.GetAsync($"/api/posts/{Slug}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        counter.Reset();
        (await anonymous.GetAsync($"/api/posts/{Slug}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        counter.Commands.Should().Be(0, "a repeated miss is answered from the cache, not by the database");

        var created = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest("Cache Key Not Yet Written", "## Body"));
        var post = (await created.Content.ReadFromJsonAsync<PostDto>())!;
        (await admin.PostAsJsonAsync($"/api/posts/{post.Id}/publish", new PublishRequest(DateTime.UtcNow.AddMinutes(-1)))).EnsureSuccessStatusCode();

        (await anonymous.GetAsync($"/api/posts/{Slug}")).StatusCode.Should().Be(HttpStatusCode.OK,
            "writing the post drops the cached 404");
    }

    private const int MaxSearchTermLength = 100;

    private static async Task<HttpClient> SeededAnonymousAsync(BlogApiFactory factory)
    {
        var admin = factory.CreateClient().WithRole("Admin");
        var created = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest("Cache Key Seed", "## Cache key seed body"));
        created.EnsureSuccessStatusCode();
        var post = (await created.Content.ReadFromJsonAsync<PostDto>())!;
        (await admin.PostAsJsonAsync($"/api/posts/{post.Id}/publish", new PublishRequest(DateTime.UtcNow.AddMinutes(-1)))).EnsureSuccessStatusCode();
        return factory.CreateClient().AsAnonymous();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Yield();
        condition().Should().BeTrue(what);
    }

    private BlogApiFactory CreateFactory(DbCommandInterceptor interceptor)
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);
        return new BlogApiFactory(
            fixture.ConnectionString,
            storagePath,
            configureServices: services => services.RemoveAll<IHostedService>(),
            commandInterceptor: interceptor);
    }
}
