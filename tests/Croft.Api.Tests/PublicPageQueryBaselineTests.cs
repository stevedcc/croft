using Croft.Api.Tests.Fixtures;
using Croft.Shared.Contracts;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System.Data.Common;

namespace Croft.Api.Tests;

/// <summary>
/// Brief 09b item 1's measurement turned into item 6's query budget: how many SQL commands each
/// API endpoint a public page render touches costs, measured against the brief's seed (3
/// published posts in 2 categories with 3 tags, 1 scheduled post, 1 draft) and asserted at most
/// the values measured after items 2-4. Every request measured here is a first-time anonymous
/// request, so a per-instance cache (item 4) cannot hide the cost: the budget governs what a
/// fresh visitor's page costs, not what a repeat hit saves. The Web side of the budget — API
/// calls per page render at most 3/4/4/4 — is pinned by PublicPageApiCallBaselineTests' exact
/// endpoint-set assertions.
/// </summary>
public class PublicPageQueryBaselineTests(PostgresDatabaseFixture fixture, ITestOutputHelper output)
    : IClassFixture<PostgresDatabaseFixture>
{
    [Fact]
    public async Task EveryPublicEndpoint_TheFourPagesTouch_RunsItsMeasuredSqlCommandCount()
    {
        var recorder = new PerRequestSqlCommandRecorder();
        await using var factory = CreateFactory(recorder);

        var admin = factory.CreateClient().WithRole("Admin");
        var seeded = await SeedAsync(admin);

        var anonymous = factory.CreateClient();
        var rows = new List<(string Page, string Endpoint, int Commands, int Budget)>();
        foreach (var (page, endpoint, budget) in EndpointsToMeasure(seeded))
        {
            recorder.Reset();
            using var response = await anonymous.GetAsync(endpoint);
            response.EnsureSuccessStatusCode();
            recorder.Commands.Should().BeGreaterThanOrEqualTo(1,
                $"{endpoint} is served from the database, so a first-time request must run SQL");
            recorder.Commands.Should().BeLessThanOrEqualTo(budget,
                $"{endpoint} must stay at or below the budget measured after items 2-4");
            rows.Add((page, endpoint, recorder.Commands, budget));
        }

        rows.Should().HaveCount(9,
            "the four public pages plus the per-render sidebar reads touch exactly these endpoints");

        output.WriteLine(string.Empty);
        output.WriteLine("SQL commands per API endpoint (brief 09b item 6, the budget):");
        output.WriteLine("| Page | API endpoint | SQL commands | Budget |");
        output.WriteLine("|---|---|---|---|");
        foreach (var row in rows.OrderBy(r => r.Page, StringComparer.Ordinal).ThenBy(r => r.Endpoint, StringComparer.Ordinal))
        {
            output.WriteLine($"| {row.Page} | `{row.Endpoint}` | {row.Commands} | {row.Budget} |");
        }
    }

    /// <summary>The endpoints a page render touches, with the SQL budget measured after items 2-4.</summary>
    private static IEnumerable<(string Page, string Endpoint, int Budget)> EndpointsToMeasure(SeededData seeded) =>
    [
        ("Home", "/api/posts?page=1&pageSize=10", 2),
        ("Post", $"/api/posts/{seeded.PostSlug}", 3),
        ("Post", $"/api/posts/{seeded.PostSlug}/related?count=3", 2),
        ("Category", $"/api/categories/{seeded.CategorySlug}", 1),
        ("Category", $"/api/posts?categorySlug={seeded.CategorySlug}&page=1&pageSize=10", 2),
        ("Tag", $"/api/tags/{seeded.TagSlug}", 1),
        ("Tag", $"/api/posts?tagSlug={seeded.TagSlug}&page=1&pageSize=10", 2),
        ("Sidebar (every page)", "/api/categories?top=12", 1),
        ("Sidebar and footer (every page)", "/api/pages/navigation/all", 1),
    ];

    private BlogApiFactory CreateFactory(PerRequestSqlCommandRecorder recorder)
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);

        // The hosted services (scheduler ticks, backfill) would run their own SQL inside the
        // measured windows; the measurement is per HTTP request, so they are removed for this
        // factory. Their behaviour is covered by their own tests.
        return new BlogApiFactory(
            fixture.ConnectionString,
            storagePath,
            configureServices: services => services.RemoveAll<IHostedService>(),
            commandInterceptor: recorder);
    }

    private static async Task<SeededData> SeedAsync(HttpClient admin)
    {
        var alpha = await CreateCategoryAsync(admin, "Baseline Alpha");
        var beta = await CreateCategoryAsync(admin, "Baseline Beta");

        // 3 published posts across the 2 categories carrying 3 tags between them.
        await PublishAsync(admin, await CreatePostAsync(admin, "Baseline Published One", alpha.Id, ["baseline-first", "baseline-second", "baseline-third"]), DateTime.UtcNow.AddDays(-3));
        await PublishAsync(admin, await CreatePostAsync(admin, "Baseline Published Two", alpha.Id, ["baseline-first"]), DateTime.UtcNow.AddDays(-2));
        var post = await PublishAsync(admin, await CreatePostAsync(admin, "Baseline Published Three", beta.Id, ["baseline-second", "baseline-third"]), DateTime.UtcNow.AddDays(-1));

        // 1 scheduled (due in two days) and 1 draft: invisible to every public read above.
        await PublishAsync(admin, await CreatePostAsync(admin, "Baseline Scheduled", beta.Id, ["baseline-first"]), DateTime.UtcNow.AddDays(2));
        await CreatePostAsync(admin, "Baseline Draft", alpha.Id, ["baseline-second"]);

        return new SeededData(post.Slug, alpha.Slug, "baseline-first");
    }

    private static async Task<CategoryDto> CreateCategoryAsync(HttpClient admin, string name)
    {
        var response = await admin.PostAsJsonAsync("/api/categories", name);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CategoryDto>())!;
    }

    private static async Task<PostDto> CreatePostAsync(HttpClient admin, string title, Guid categoryId, IReadOnlyList<string> tags)
    {
        var response = await admin.PostAsJsonAsync("/api/posts",
            new CreatePostRequest(title, "## A short baseline body", Tags: tags, CategoryId: categoryId));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PostDto>())!;
    }

    private static async Task<PostDto> PublishAsync(HttpClient admin, PostDto post, DateTime publishedAt)
    {
        var response = await admin.PostAsJsonAsync($"/api/posts/{post.Id}/publish", new PublishRequest(publishedAt));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PostDto>())!;
    }

    private sealed record SeededData(string PostSlug, string CategorySlug, string TagSlug);

    /// <summary>
    /// Counts the SQL commands of one HTTP request: the test resets it before each request and
    /// reads the count after, so per-request numbers stay exact with the serial test flow.
    /// </summary>
    private sealed class PerRequestSqlCommandRecorder : DbCommandInterceptor
    {
        private int _count;

        public int Commands => _count;

        public void Reset() => Interlocked.Exchange(ref _count, 0);

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Interlocked.Increment(ref _count);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Interlocked.Increment(ref _count);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            Interlocked.Increment(ref _count);
            return result;
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return ValueTask.FromResult(result);
        }
    }
}
