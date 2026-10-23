using AwesomeAssertions;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Croft.Shared.Warmup;
using Croft.Warmup.Contract;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Croft.Web.Tests.Warmup;

/// <summary>
/// Brief 14a item 2: the Web's warm-up request list, built at run time from the site's own content.
/// The requests really reach the Web (the TestServer's handler stands in for loopback), which reads
/// the content from a stubbed API; every test creates its own content.
/// </summary>
public sealed class WebWarmupPlanTests
{
    private static readonly Guid MediaId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTime Published = new(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task FullSite_WarmsTheTenPaths_OnceEach_InOrder()
    {
        await using var run = await WarmUpAsync(FullSite());

        run.Paths.Should().Equal(
        [
            "/",
            "/sitemap.xml",
            "/post/warm-post-one",
            $"/api/media/{MediaId}?w=800&h=400",
            "/about",
            "/category/warm-category",
            "/tag/warm-tag",
            "/categories",
            "/search?q=Warm",
            "/feed.xml",
        ], "the ten steps of the brief, each once, the later ones from what the earlier ones returned");
    }

    [Fact]
    public async Task ThePostImage_IsTheFirstMediaUrlInThePostsHtml_AsWritten_WhenThereIsNoBanner()
    {
        var api = FullSite();
        api.PostDetails[0] = api.PostDetails[0] with
        {
            BannerImage = null,
            MarkdownContent = $"Intro\n\n![a picture](/api/media/{MediaId}?w=320&h=200)\n",
        };

        await using var run = await WarmUpAsync(api);

        run.Paths.Should().Contain($"/api/media/{MediaId}?w=320&h=200",
            "the body image is warmed exactly as written, query included (HTML-decoded)");
    }

    [Fact]
    public async Task EmptySite_WarmsOnlyTheContentFreePaths_AndLogsNoWarning()
    {
        await using var run = await WarmUpAsync(new StubBlogApiHandler());

        run.Paths.Should().Equal(["/", "/sitemap.xml", "/categories", "/search?q=blog", "/feed.xml"],
            "an empty site has no post, page, category or tag to warm, and no image");
        run.Logs.Where(e => e.Level >= LogLevel.Warning).Should().BeEmpty("missing content is skipped, not failed");
    }

    [Fact]
    public async Task WarmupRequests_AreNeverCountedAsPageViews()
    {
        var api = FullSite();

        await using var run = await WarmUpAsync(api);

        api.Requests.Select(r => r.PathAndQuery).Should().NotContain(p => p.Contains("analytics", StringComparison.Ordinal),
            "page views are counted only by the browser's beacon; no warm-up request is a beacon");
    }

    private static StubBlogApiHandler FullSite()
    {
        var category = new CategoryDto(Guid.CreateVersion7(), "Warm Category", "warm-category", null, 1);
        var tag = new TagDto(Guid.CreateVersion7(), "Warm Tag", "warm-tag", 1, CountsTowardsRelated: true);
        var banner = new MediaAssetDto(MediaId, "banner.png", "banner.png", "image/png",
            $"/api/media/{MediaId}?w=800&h=400", MediaAssetScope.PostSpecific, null, "A banner");
        return new StubBlogApiHandler
        {
            Posts =
            [
                new PostSummaryDto(Guid.CreateVersion7(), "warm-post-one", "Warm Post One", null, "An excerpt", "A summary", 1,
                    PostStatus.Published, Published, Published, "Author", category, [tag],
                    new BannerRefDto(banner.Url, banner.AltText), null, null),
            ],
            Categories = [category],
            Tags = [tag],
            Pages = [new PageSummaryDto(Guid.CreateVersion7(), "about", "About", PostStatus.Published, Published)],
            PostDetails =
            [
                new PostDto(Guid.CreateVersion7(), "warm-post-one", "Warm Post One", null, "## Body\n\nSome text.",
                    "An excerpt", "A summary", PostStatus.Published, Published, Published, Published,
                    "author-id", "Author", banner, [tag], category, 1),
            ],
        };
    }

    private static async Task<WarmRun> WarmUpAsync(StubBlogApiHandler api)
    {
        var recorder = new RecordingHandler();
        var loopback = new LateBoundHandler();
        var setup = new WarmupHostSetup();
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("Blog:BaseUrl", "https://example.com/");
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient("BlogApi").ConfigurePrimaryHttpMessageHandler(() => api);
                // The warm-up's loopback requests go to this very host through the TestServer.
                services.AddHttpClient(WarmupRunner.ClientName)
                    .AddHttpMessageHandler(() => recorder)
                    .ConfigurePrimaryHttpMessageHandler(() => loopback);
            });
            setup.Apply(builder);
        });
        loopback.Ready(factory.Server.CreateHandler());
        await factory.Services.GetRequiredService<WarmupGate>().WhenOpen.WaitAsync(TimeSpan.FromSeconds(60));
        return new WarmRun(factory, recorder, setup.Logs);
    }

    private sealed class WarmRun(IDisposable factory, RecordingHandler recorder, WarmupLogCapture logs) : IAsyncDisposable
    {
        public IReadOnlyList<string> Paths => [.. recorder.Paths];

        public IReadOnlyList<CapturedLog> Logs => [.. logs.FromRunner];

        public ValueTask DisposeAsync()
        {
            factory.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
