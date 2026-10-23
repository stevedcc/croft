using System.Globalization;
using Croft.Shared.Contracts;

namespace Croft.Web.E2E;

/// <summary>
/// Brief 07g item 3's public page end to end: a published page renders its title, date line and
/// body at 1440×900 and 390×844; its body column lands on the same left/right edges as a post
/// body's (±1px); an unknown single-segment /{slug} answers HTTP 404 with the not-found copy
/// (which also proves the "/{slug}" route outranks NotFound's "/{**path:nonfile}" catch-all);
/// the sitemap lists the page while the feed does not, and the page is never served from the
/// post route: /post/{slug} answers 404 with the not-found copy while the same page renders at
/// /{slug} — one canonical URL per page, not duplicate content with conflicting SEO signals.
/// The fixtures are created through the
/// API as the mock admin (MockIdpApi) — the pages editor is a later brief. The "Updated" date
/// rule cannot be exercised here (this suite has no API test hook and no database helper to set
/// ContentEditedAt), so it lives in the Web unit suite (bUnit: PostMetaTests, PostHeroTests,
/// PageSurfaceTests).
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class StaticPageTests : WebTest
{
    private static readonly SemaphoreSlim DataGate = new(1, 1);
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];
    private static readonly CultureInfo EnGb = CultureInfo.GetCultureInfo("en-GB");

    private static bool _dataReady;
    private static string _pageSlug = string.Empty;
    private static string _postSlug = string.Empty;
    private static string _publishedDateLine = string.Empty;

    [TestCase(1440, 900)]
    [TestCase(390, 844)]
    public async Task Page_RendersTitleDateLineAndBody(int width, int height)
    {
        await EnsureFixturesAsync();
        await Page.SetViewportSizeAsync(width, height);
        await Page.GotoAsync($"{BaseUrl}/{_pageSlug}");
        await Expect(Page.Locator(".markdown-body")).ToBeVisibleAsync();

        Assert.That((await Page.Locator(".page-surface__title").TextContentAsync())?.Trim(),
            Is.EqualTo($"About {RunId}"), "the page's title is the plain title header's h1");
        Assert.That((await Page.Locator(".page-surface__subtitle").TextContentAsync())?.Trim(),
            Is.EqualTo("A page fixture"), "the subtitle renders under the title");
        Assert.That((await Page.Locator(".post-meta__when").TextContentAsync())?.Trim(),
            Is.EqualTo(_publishedDateLine), "the date line is the page's published date");
        Assert.That(await Page.Locator(".markdown-body").TextContentAsync(),
            Does.Contain($"Pagemarker {RunId}"), "the Markdown body renders in the reading column");
    }

    [TestCase(1440, 900)]
    [TestCase(390, 844)]
    public async Task PageBodyColumn_MatchesThePostBodyColumn(int width, int height)
    {
        await EnsureFixturesAsync();
        await Page.SetViewportSizeAsync(width, height);

        await Page.GotoAsync($"{BaseUrl}/{_pageSlug}");
        await Expect(Page.Locator(".markdown-body")).ToBeVisibleAsync();
        await Page.EvaluateAsync("() => document.fonts.ready");
        var pageColumn = await BodyColumnAsync();

        await Page.GotoAsync($"{BaseUrl}/post/{_postSlug}");
        await Expect(Page.Locator(".markdown-body")).ToBeVisibleAsync();
        await Page.EvaluateAsync("() => document.fonts.ready");
        var postColumn = await BodyColumnAsync();

        Assert.That(pageColumn.Left, Is.EqualTo(postColumn.Left).Within(1),
            $"at {width}×{height} the page's body column starts where the post body's does " +
            $"(page left {pageColumn.Left:0.##}, post left {postColumn.Left:0.##})");
        Assert.That(pageColumn.Right, Is.EqualTo(postColumn.Right).Within(1),
            $"at {width}×{height} the page's body column ends where the post body's does " +
            $"(page right {pageColumn.Right:0.##}, post right {postColumn.Right:0.##})");
    }

    [Test]
    public async Task UnknownPageSlug_Returns404WithNotFoundCopy()
    {
        // Unique per run: no page can ever hold this slug. Single segment, so the outcome also
        // proves StaticPage outranks NotFound's catch-all and renders the not-found page itself.
        await using var response = await Page.APIRequest.GetAsync($"{BaseUrl}/no-such-page-{Guid.NewGuid():N}");

        Assert.That(response.Status, Is.EqualTo(404),
            "an unknown /<slug> must answer with HTTP 404 (NavigationManager.NotFound), not a redirect.");
        var body = await response.TextAsync();
        Assert.That(body, Does.Contain("This page has wandered off"),
            "the 404 response must render the not-found copy (spec §5.5).");
    }

    [Test]
    public async Task Page_IsNotServedFromThePostRoute_ButRendersAtItsRootSlug()
    {
        await EnsureFixturesAsync();

        await using var duplicate = await Page.APIRequest.GetAsync($"{BaseUrl}/post/{_pageSlug}");
        Assert.That(duplicate.Status, Is.EqualTo(404),
            "the /post/ route is posts-only: a published page there is duplicate content and must " +
            "answer HTTP 404 (NavigationManager.NotFound), not render as a post.");
        var duplicateBody = await duplicate.TextAsync();
        Assert.That(duplicateBody, Does.Contain("This page has wandered off"),
            "the 404 response must render the not-found copy (spec §5.5).");
        Assert.That(duplicateBody, Does.Not.Contain($"Pagemarker {RunId}"),
            "the page's content must not leak through the post route.");

        await using var canonical = await Page.APIRequest.GetAsync($"{BaseUrl}/{_pageSlug}");
        Assert.That(canonical.Status, Is.EqualTo(200), "the page still renders at its canonical URL.");
        Assert.That(await canonical.TextAsync(), Does.Contain($"Pagemarker {RunId}"),
            "the page's body lives at /{slug} — the one canonical URL.");
    }

    [Test]
    public async Task Sitemap_ListsThePage_ButTheFeedDoesNot()
    {
        await EnsureFixturesAsync();

        await using var sitemap = await Page.APIRequest.GetAsync($"{BaseUrl}/sitemap.xml");
        Assert.That(sitemap.Status, Is.EqualTo(200));
        Assert.That(await sitemap.TextAsync(), Does.Contain($"/{_pageSlug}</loc>"),
            "the sitemap lists the published page at its root slug");

        await using var feed = await Page.APIRequest.GetAsync($"{BaseUrl}/feed.xml");
        Assert.That(feed.Status, Is.EqualTo(200));
        Assert.That(await feed.TextAsync(), Does.Not.Contain(_pageSlug),
            "pages are not posts and must stay out of the feed");
    }

    /// <summary>The rendered body column's viewport edges.</summary>
    private async Task<(double Left, double Right)> BodyColumnAsync()
    {
        var json = await Page.EvaluateAsync<string>(
            @"() => { const r = document.querySelector('.markdown-body').getBoundingClientRect();
                      return JSON.stringify({ left: r.left, right: r.right }); }");
        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        return (doc.RootElement.GetProperty("left").GetDouble(), doc.RootElement.GetProperty("right").GetDouble());
    }

    /// <summary>One published page and one published post per run, created through the API as the
    /// mock admin (the pages editor is a later brief): the page carries the date line and body
    /// markers, the post a body for the body-column comparison.</summary>
    private async Task EnsureFixturesAsync()
    {
        if (_dataReady)
        {
            return;
        }

        await DataGate.WaitAsync();
        try
        {
            if (_dataReady)
            {
                return;
            }

            using var client = MockIdpApi.CreateClient();
            var token = await MockIdpApi.GetAdminAccessTokenAsync(client);

            var page = await MockIdpApi.PostJsonAsync(client, ApiUrl, "/api/posts", new CreatePostRequest(
                $"About {RunId}",
                $"## Hello\n\nPagemarker {RunId} waits in the reading column.",
                Subtitle: "A page fixture",
                Kind: Croft.Shared.Enums.PostKind.Page), token);
            var pageId = page.RootElement.GetProperty("id").GetString()!;
            var pagePublished = await MockIdpApi.PostJsonAsync(
                client, ApiUrl, $"/api/posts/{pageId}/publish", "{}", token);
            _pageSlug = pagePublished.RootElement.GetProperty("slug").GetString()!;
            var publishedAt = pagePublished.RootElement.GetProperty("publishedAt").GetDateTimeOffset().UtcDateTime;
            _publishedDateLine = publishedAt.ToString("d MMMM yyyy", EnGb);

            var post = await MockIdpApi.PostJsonAsync(client, ApiUrl, "/api/posts", new CreatePostRequest(
                $"Post Fixture {RunId}",
                $"## First section\n\nPostmarker {RunId} fills the first section of the body.\n\n" +
                $"## Second section\n\nPostmarker {RunId} fills the second section of the body."), token);
            var postId = post.RootElement.GetProperty("id").GetString()!;
            var postPublished = await MockIdpApi.PostJsonAsync(
                client, ApiUrl, $"/api/posts/{postId}/publish", "{}", token);
            _postSlug = postPublished.RootElement.GetProperty("slug").GetString()!;

            _dataReady = true;
        }
        finally
        {
            DataGate.Release();
        }
    }
}
