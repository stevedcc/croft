using System.Text.RegularExpressions;

namespace Croft.Web.E2E;

/// <summary>
/// Static assets are served by MapStaticAssets and referenced with @Assets, so their URLs carry a
/// content fingerprint. The assertions are on the network responses the browser actually receives
/// (Playwright response events), not on &lt;link&gt; tags merely being present in the markup: a
/// stylesheet link in the HTML proves nothing about whether the stylesheet was served.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class StaticAssetTests : WebTest
{
    private static readonly Regex AppStylesheetPath = new(@"/app(\.[A-Za-z0-9_-]+)?\.css$", RegexOptions.Compiled);
    private static readonly Regex FingerprintedAppStylesheetPath = new(@"/app\.[A-Za-z0-9_-]+\.css$", RegexOptions.Compiled);
    private static readonly Regex MudBlazorStylesheetPath = new(@"/_content/MudBlazor/MudBlazor\.min(\.[A-Za-z0-9_-]+)?\.css$", RegexOptions.Compiled);

    /// <summary>The post-page-only assets: post.js (→ highlight.js core + grammars) and the
    /// highlight theme. List pages must never request any of them.</summary>
    private static readonly Regex PostAssetPath = new(
        @"/post\.js$|/highlightjs/|/highlight-theme\.css$", RegexOptions.Compiled);

    [Test]
    public async Task HomePage_OwnStylesheet_IsServedWith200_AndMudBlazorStylesheetIsNotFetched()
    {
        // Brief 09i: a reading page is static SSR and uses no MudBlazor, so it no longer loads
        // MudBlazor's stylesheet (the interactive documents still do: next test).
        var responses = await LoadHomePageAsync();

        await AssertServedWith200Async(responses, "app stylesheet", AppStylesheetPath);
        Assert.That(responses.Where(r => MudBlazorStylesheetPath.IsMatch(UrlPath(r))).Select(r => r.Url), Is.Empty,
            "the home page must not fetch MudBlazor's stylesheet.");
    }

    [Test]
    public async Task InteractivePage_OwnStylesheetAndMudBlazorStylesheet_AreServedWith200()
    {
        // An interactive page is a staff page since brief 13a (the not-found page is static).
        await LoginAsync("admin@example.com", "password");
        using var responseLog = EventLog.Responses(Page);
        await Page.GotoAsync($"{BaseUrl}/admin/posts");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        responseLog.Stop();
        var responses = responseLog.Snapshot();

        await AssertServedWith200Async(responses, "app stylesheet", AppStylesheetPath);
        await AssertServedWith200Async(responses, "MudBlazor stylesheet", MudBlazorStylesheetPath);
    }

    [Test]
    public async Task HomePage_AppStylesheetUrl_IsFingerprinted()
    {
        var responses = await LoadHomePageAsync();

        var matches = responses.Where(r => AppStylesheetPath.IsMatch(UrlPath(r))).ToList();
        Assert.That(matches, Has.Count.EqualTo(1), "The home page must fetch exactly one app stylesheet.");
        Assert.That(matches[0].Url, Does.Match(FingerprintedAppStylesheetPath),
            $"The app stylesheet URL must carry a content fingerprint. URL: {matches[0].Url}");
    }

    [Test]
    public async Task PostAssets_LoadOnAPostPage_NeverOnTheHomePage()
    {
        var homeResponses = await LoadHomePageAsync();
        Assert.That(homeResponses.Where(r => PostAssetPath.IsMatch(UrlPath(r))).Select(r => r.Url), Is.Empty,
            "the list pages must not request post.js, highlight.js or the highlight theme");

        // The post page loads exactly those assets — the same behaviour, per page. The post is
        // this test's own fixture, created through the admin UI (the suite's fixture rule):
        // proving the per-page load must not need another test's post to have run first.
        var slug = await CreatePublishedPostAsync();
        Assert.That(slug, Is.Not.Null.And.Not.Empty, "A published post is needed to prove the per-page load.");

        using var responseLog = EventLog.Responses(Page);
        await Page.GotoAsync($"{BaseUrl}/post/{slug}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        responseLog.Stop();
        var responses = responseLog.Snapshot();

        foreach (var expected in new[] { "/post.js", "/highlightjs/", "/highlight-theme.css" })
        {
            var matches = responses.Where(r => UrlPath(r).Contains(expected, StringComparison.Ordinal)).ToList();
            Assert.That(matches, Is.Not.Empty, $"the post page must request the asset {expected}");
            foreach (var match in matches)
            {
                Assert.That(await FreshStatusAsync(match.Url), Is.EqualTo(200),
                    $"every {expected} response must be served with 200. URL: {match.Url}");
            }
        }
    }

    /// <summary>A published post of this test's own, created through the admin editor (ruling
    /// R4-auth: fixtures go through the admin UI); its slug comes from the public API via
    /// <see cref="WebTest.SlugForPostAsync"/>, which asserts the post is served.</summary>
    private async Task<string> CreatePublishedPostAsync()
    {
        await LoginAsync("admin@example.com", "password");

        await OpenNewPostEditorAsync();

        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync($"Static Asset Post {Guid.NewGuid():N}");
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).BlurAsync();
        await Page.GetByLabel("Markdown").FillAsync("Static-asset fixture body.");
        await Page.GetByLabel("Markdown").BlurAsync();

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Publish now" }).ClickAsync();
        await Page.WaitForURLAsync(
            url => url.StartsWith($"{BaseUrl}/admin/posts/") && url.EndsWith("/edit"),
            new PageWaitForURLOptions { Timeout = 15000 });

        return await SlugForPostAsync(PostIdFromEditorUrl());
    }

    private async Task AssertServedWith200Async(IReadOnlyList<IResponse> responses, string label, Regex pathPattern)
    {
        var matches = responses.Where(r => pathPattern.IsMatch(UrlPath(r))).ToList();
        Assert.That(matches, Has.Count.EqualTo(1), $"The page must fetch exactly one {label}.");
        Assert.That(await FreshStatusAsync(matches[0].Url), Is.EqualTo(200), $"The {label} must be served with 200. URL: {matches[0].Url}");
    }

    /// <summary>
    /// The status of a delivery of the URL the page requested, fetched again without the browser's
    /// cache. The page's own response can be a 304 when the browser revalidates an asset it already
    /// holds (Development serves static assets with no-cache, and .NET 11's MapStaticAssets answers
    /// If-None-Match; .NET 10 always sent 200), which proves nothing about the asset being served.
    /// </summary>
    private async Task<int> FreshStatusAsync(string url)
    {
        await using var response = await Page.APIRequest.GetAsync(url);
        return response.Status;
    }

    /// <summary>The home page and every network response its load triggers (stylesheets included).</summary>
    private async Task<IReadOnlyList<IResponse>> LoadHomePageAsync()
    {
        using var responseLog = EventLog.Responses(Page);
        await Page.GotoAsync(BaseUrl);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        responseLog.Stop();
        return responseLog.Snapshot();
    }

    private static string UrlPath(IResponse response) => new Uri(response.Url).AbsolutePath;
}
