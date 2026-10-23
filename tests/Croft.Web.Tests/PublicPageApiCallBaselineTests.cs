using System.Net;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Croft.Web.Tests;

/// <summary>
/// Brief 09b item 1's Web-side baseline: how many API calls the anonymous render of each public
/// page makes, counted by a <see cref="CountingHandler"/> wrapped around the BlogApi HttpClient's
/// stubbed primary handler. The table this prints is the "before"; item 6 turns the re-measured
/// numbers into the page budget.
/// </summary>
public class PublicPageApiCallBaselineTests(ITestOutputHelper output)
{
    [Fact]
    public async Task EveryPublicPage_MakesItsMeasuredApiCallsPerAnonymousRender()
    {
        var api = SeededApi();
        var counter = new CountingHandler();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("Blog:BaseUrl", "https://example.com/");
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient("BlogApi")
                    .ConfigurePrimaryHttpMessageHandler(() => api)
                    .AddHttpMessageHandler(() => counter);
            });
        });
        using var client = factory.CreateClient();

        var rows = new List<(string Page, string Path, IReadOnlyList<string> Calls)>();
        foreach (var (page, path) in PagesToRender())
        {
            counter.Requests.Clear();
            using var response = await client.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.OK,
                $"{path} must render anonymously — the measurement is of a working page");
            var body = await response.Content.ReadAsStringAsync();
            body.Should().Contain("Baseline",
                $"{path} must render the seeded content, not an error or empty state");

            rows.Add((page, path, [.. counter.Requests]));
        }

        output.WriteLine(string.Empty);
        output.WriteLine("API calls per anonymous page render (brief 09b item 1, the 'before'):");
        output.WriteLine("| Page | API calls | Endpoints |");
        output.WriteLine("|---|---|---|");
        foreach (var row in rows)
        {
            var endpoints = string.Join("<br>", row.Calls.Select(c => $"`{c}`"));
            output.WriteLine($"| {row.Page} (`{row.Path}`) | {row.Calls.Count} | {endpoints} |");
        }

        var byPage = rows.ToDictionary(r => r.Path, r => r.Calls);
        byPage["/"].Should().BeEquivalentTo(
            ["/api/posts?page=1&pageSize=10&publicView=true", "/api/categories?top=12", "/api/pages/navigation/all"],
            "home renders its listing plus the per-render sidebar reads");
        byPage[$"/post/{PostSlug}"].Should().BeEquivalentTo(
            [$"/api/posts/{PostSlug}", $"/api/posts/{PostSlug}/related?count=3", $"/api/posts/{PostSlug}/discussion",
                "/api/categories?top=12", "/api/pages/navigation/all"],
            "the post page renders its post, its related list, its discussion (brief 12b) and the sidebar reads");
        byPage[$"/category/{CategorySlug}"].Should().BeEquivalentTo(
            [$"/api/categories/{CategorySlug}", $"/api/posts?categorySlug={CategorySlug}&page=1&pageSize=10&publicView=true",
                "/api/categories?top=12", "/api/pages/navigation/all"],
            "the category page resolves its category, lists its posts and renders the sidebar");
        byPage[$"/tag/{TagSlug}"].Should().BeEquivalentTo(
            [$"/api/tags/{TagSlug}", $"/api/posts?tagSlug={TagSlug}&page=1&pageSize=10&publicView=true",
                "/api/categories?top=12", "/api/pages/navigation/all"],
            "the tag page resolves its tag, lists its posts and renders the sidebar");
    }

    private const string PostSlug = "baseline-published-one";
    private const string CategorySlug = "baseline-alpha";
    private const string TagSlug = "baseline-first";

    private static IEnumerable<(string Page, string Path)> PagesToRender() =>
    [
        ("Home", "/"),
        ("Post", $"/post/{PostSlug}"),
        ("Category", $"/category/{CategorySlug}"),
        ("Tag", $"/tag/{TagSlug}"),
    ];

    /// <summary>The API over the stub, seeded with the brief's shape: 3 published posts in
    /// 2 categories with 3 tags between them.</summary>
    private static StubBlogApiHandler SeededApi()
    {
        var alpha = new CategoryDto(Guid.CreateVersion7(), "Baseline Alpha", CategorySlug, null, 2);
        var beta = new CategoryDto(Guid.CreateVersion7(), "Baseline Beta", "baseline-beta", null, 1);
        var first = new TagDto(Guid.CreateVersion7(), "Baseline First", TagSlug, 2, CountsTowardsRelated: true);
        var second = new TagDto(Guid.CreateVersion7(), "Baseline Second", "baseline-second", 2, CountsTowardsRelated: true);
        var third = new TagDto(Guid.CreateVersion7(), "Baseline Third", "baseline-third", 2, CountsTowardsRelated: true);

        var publishedAt = new DateTime(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc);
        var updatedAt = new DateTime(2026, 10, 11, 9, 0, 0, DateTimeKind.Utc);
        PostSummaryDto Summary(Guid id, string slug, string title, CategoryDto category, IReadOnlyList<TagDto> tags) =>
            new(id, slug, title, null, "A baseline excerpt", "A baseline summary", 1,
                PostStatus.Published, publishedAt, updatedAt, "Baseline Author", category, tags, null, null, null);

        var posts = new List<PostSummaryDto>
        {
            Summary(Guid.CreateVersion7(), PostSlug, "Baseline Published One", alpha, [first, second, third]),
            Summary(Guid.CreateVersion7(), "baseline-published-two", "Baseline Published Two", alpha, [first]),
            Summary(Guid.CreateVersion7(), "baseline-published-three", "Baseline Published Three", beta, [second, third]),
        };

        return new StubBlogApiHandler
        {
            Posts = posts,
            Categories = [alpha, beta],
            Tags = [first, second, third],
            PostDetails =
            [
                new PostDto(Guid.CreateVersion7(), PostSlug, "Baseline Published One", null,
                    "## A short baseline body", "A baseline excerpt", "A baseline summary",
                    PostStatus.Published, publishedAt, publishedAt, updatedAt,
                    "baseline-author-id", "Baseline Author", null, [first, second, third], alpha, 1),
            ],
        };
    }

    /// <summary>Counts every request the BlogApi HttpClient issues, in order.</summary>
    private sealed class CountingHandler : DelegatingHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
