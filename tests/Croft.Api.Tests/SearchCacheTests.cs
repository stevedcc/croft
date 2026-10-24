using Croft.Api.Services;
using Croft.Api.Tests.Fixtures;
using Croft.Shared.Contracts;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Croft.Api.Tests;

/// <summary>
/// Brief 14b item 3 (architecture finding 11): an anonymous search goes through the public read
/// cache, so a repeated search runs no full-text query. The key is built like the list's search
/// term (normalised, page and size clamped); staff searches are never cached.
/// </summary>
public class SearchCacheTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private static async Task PublishAsync(HttpClient admin, string title, string body)
    {
        var created = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest(title, body));
        created.EnsureSuccessStatusCode();
        var post = (await created.Content.ReadFromJsonAsync<PostDto>())!;
        (await admin.PostAsJsonAsync($"/api/posts/{post.Id}/publish", new PublishRequest(DateTime.UtcNow.AddMinutes(-1))))
            .EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task TheSameAnonymousSearchTwice_RunsTheFullTextQueryOnce()
    {
        var counter = new SqlCommandCounter();
        await using var scratch = await ScratchDatabase.CreateAsync(fixture.ConnectionString);
        await using var factory = CreateFactory(scratch, counter);
        await PublishAsync(factory.CreateClient().WithRole("Admin"), "Raspberry Pi Cluster", "Notes on a raspberry cluster.");
        var anonymous = factory.CreateClient().AsAnonymous();

        counter.Reset();
        var first = await anonymous.GetFromJsonAsync<PagedResult<PostSummaryDto>>("/api/search?q=raspberry&publicView=true");
        var oneSearch = counter.Commands;
        var second = await anonymous.GetFromJsonAsync<PagedResult<PostSummaryDto>>("/api/search?q=raspberry&publicView=true");

        first!.Items.Should().ContainSingle();
        oneSearch.Should().BeGreaterThan(0, "the first search queries");
        counter.Commands.Should().Be(oneSearch, "the repeat is answered from the cache");
        second.Should().BeEquivalentTo(first);
    }

    [Fact]
    public async Task TermsThatDifferOnlyInCaseAndSpacing_AreOneEntry()
    {
        var counter = new SqlCommandCounter();
        await using var scratch = await ScratchDatabase.CreateAsync(fixture.ConnectionString);
        await using var factory = CreateFactory(scratch, counter);
        await PublishAsync(factory.CreateClient().WithRole("Admin"), "Pi Cluster Notes", "A pi cluster at home.");
        var anonymous = factory.CreateClient().AsAnonymous();

        (await anonymous.GetAsync("/api/search?q=Pi++Cluster")).EnsureSuccessStatusCode();
        var cache = (MemoryPublicReadCache)factory.Services.GetRequiredService<IPublicReadCache>();
        var entries = cache.Count;
        counter.Reset();
        var again = await anonymous.GetFromJsonAsync<PagedResult<PostSummaryDto>>("/api/search?q=%20pi%20cluster%09");

        again!.Items.Should().ContainSingle();
        counter.Commands.Should().Be(0, "\"Pi  Cluster\" and \"pi cluster\" are one key");
        cache.Count.Should().Be(entries);
    }

    [Fact]
    public async Task PageSizesAboveTheMaximum_ShareOneEntryWithTheMaximum()
    {
        var counter = new SqlCommandCounter();
        await using var scratch = await ScratchDatabase.CreateAsync(fixture.ConnectionString);
        await using var factory = CreateFactory(scratch, counter);
        await PublishAsync(factory.CreateClient().WithRole("Admin"), "Sized Search", "Sized search body.");
        var anonymous = factory.CreateClient().AsAnonymous();

        (await anonymous.GetAsync("/api/search?q=sized&pageSize=10000")).EnsureSuccessStatusCode();
        counter.Reset();
        (await anonymous.GetAsync("/api/search?q=sized&pageSize=100")).EnsureSuccessStatusCode();
        (await anonymous.GetAsync("/api/search?q=sized&pageSize=101")).EnsureSuccessStatusCode();

        counter.Commands.Should().Be(0, "the query clamps the size to 100, so the key does too");
    }

    [Theory]
    [InlineData("/api/search?q=staffterm&publicView=true")]
    [InlineData("/api/search?q=staffterm")]
    public async Task AStaffSearch_IsNeverCached(string url)
    {
        var counter = new SqlCommandCounter();
        await using var scratch = await ScratchDatabase.CreateAsync(fixture.ConnectionString);
        await using var factory = CreateFactory(scratch, counter);
        var admin = factory.CreateClient().WithRole("Admin");
        await PublishAsync(admin, "Staff Search Subject", "A staffterm in the body.");

        counter.Reset();
        (await admin.GetAsync(url)).EnsureSuccessStatusCode();
        var oneSearch = counter.Commands;
        (await admin.GetAsync(url)).EnsureSuccessStatusCode();

        oneSearch.Should().BeGreaterThan(0);
        counter.Commands.Should().Be(2 * oneSearch, "staff read their own writes, whatever view they ask for");
    }

    [Fact]
    public async Task ASearchWithNoResults_IsCachedAsANegative()
    {
        var counter = new SqlCommandCounter();
        await using var scratch = await ScratchDatabase.CreateAsync(fixture.ConnectionString);
        await using var factory = CreateFactory(scratch, counter);
        var anonymous = factory.CreateClient().AsAnonymous();
        var cache = (MemoryPublicReadCache)factory.Services.GetRequiredService<IPublicReadCache>();
        var negatives = cache.NegativeCount;

        (await anonymous.GetAsync("/api/search?q=nothingmatchesthis")).EnsureSuccessStatusCode();
        counter.Reset();
        var again = await anonymous.GetFromJsonAsync<PagedResult<PostSummaryDto>>("/api/search?q=nothingmatchesthis");

        again!.Items.Should().BeEmpty();
        cache.NegativeCount.Should().Be(negatives + 1, "an empty result goes through the capped negative path");
        counter.Commands.Should().Be(0);
    }

    [Fact]
    public async Task RandomEmptySearches_CannotTakeMoreThanTheNegativeBudget()
    {
        await using var scratch = await ScratchDatabase.CreateAsync(fixture.ConnectionString);
        await using var factory = CreateFactory(scratch, new SqlCommandCounter());
        var anonymous = factory.CreateClient().AsAnonymous();
        var cache = (MemoryPublicReadCache)factory.Services.GetRequiredService<IPublicReadCache>();

        for (var i = 0; i < MemoryPublicReadCache.DefaultMaxNegativeEntries + 25; i++)
        {
            (await anonymous.GetAsync($"/api/search?q=zzterm{i}")).EnsureSuccessStatusCode();
        }

        cache.NegativeCount.Should().BeLessThanOrEqualTo(MemoryPublicReadCache.DefaultMaxNegativeEntries);
    }

    [Fact]
    public async Task APageBeyondTheFirstFew_IsNotCached()
    {
        var counter = new SqlCommandCounter();
        await using var scratch = await ScratchDatabase.CreateAsync(fixture.ConnectionString);
        await using var factory = CreateFactory(scratch, counter);
        await PublishAsync(factory.CreateClient().WithRole("Admin"), "Deep Search", "Deep search body.");
        var anonymous = factory.CreateClient().AsAnonymous();
        var cache = (MemoryPublicReadCache)factory.Services.GetRequiredService<IPublicReadCache>();

        (await anonymous.GetAsync("/api/search?q=deep&page=4")).EnsureSuccessStatusCode();
        var entries = cache.Count;
        counter.Reset();
        (await anonymous.GetAsync("/api/search?q=deep&page=4")).EnsureSuccessStatusCode();

        counter.Commands.Should().BeGreaterThan(0, "nobody pages deep through a search, and a crawler must not fill the cache with it");
        cache.Count.Should().Be(entries);
    }

    [Fact]
    public async Task APageNobodyServes_AnswersEmptyWithoutQueryingOrCaching()
    {
        var counter = new SqlCommandCounter();
        await using var scratch = await ScratchDatabase.CreateAsync(fixture.ConnectionString);
        await using var factory = CreateFactory(scratch, counter);
        var anonymous = factory.CreateClient().AsAnonymous();
        var cache = (MemoryPublicReadCache)factory.Services.GetRequiredService<IPublicReadCache>();
        var entries = cache.Count;
        counter.Reset();

        var result = await anonymous.GetFromJsonAsync<PagedResult<PostSummaryDto>>("/api/search?q=anything&page=999999");

        result!.Items.Should().BeEmpty();
        counter.Commands.Should().Be(0);
        cache.Count.Should().Be(entries);
    }

    [Fact]
    public async Task AfterAPublishThatMatches_TheNextAnonymousSearchFindsIt()
    {
        var counter = new SqlCommandCounter();
        await using var scratch = await ScratchDatabase.CreateAsync(fixture.ConnectionString);
        await using var factory = CreateFactory(scratch, counter);
        var admin = factory.CreateClient().WithRole("Admin");
        var anonymous = factory.CreateClient().AsAnonymous();

        var before = await anonymous.GetFromJsonAsync<PagedResult<PostSummaryDto>>("/api/search?q=latecomer");
        await PublishAsync(admin, "The Latecomer", "A latecomer arrives.");
        var after = await anonymous.GetFromJsonAsync<PagedResult<PostSummaryDto>>("/api/search?q=latecomer");

        before!.Items.Should().BeEmpty();
        after!.Items.Should().ContainSingle(p => p.Title == "The Latecomer", "a publish invalidates the cached search, the empty one included");
    }

    private static BlogApiFactory CreateFactory(ScratchDatabase scratch, DbCommandInterceptor interceptor)
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);
        return new BlogApiFactory(
            scratch.ConnectionString,
            storagePath,
            configureServices: services => services.RemoveAll<IHostedService>(),
            commandInterceptor: interceptor);
    }
}
