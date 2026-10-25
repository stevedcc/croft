using Croft.Shared.Enums;

namespace Croft.Web.E2E;

/// <summary>
/// Brief 09i item 1: the seven static reading pages (Home, Post, Category, Tag, Categories,
/// StaticPage, Search) download no MudBlazor stylesheet or script and their HTML carries nothing
/// of MudBlazor (no <c>mud-</c> class, no provider style block), while the interactive documents
/// (an admin page) still load both and still render MudBlazor components. The
/// outcomes asserted are the requests the browser actually made and the markup it received.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class NoMudBlazorOnReadingPagesTests : StaticReadingTest
{
    private static readonly SemaphoreSlim DataGate = new(1, 1);
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];

    private static bool _dataReady;
    private static string _postSlug = string.Empty;
    private static string _categorySlug = string.Empty;
    private static string _tagSlug = string.Empty;
    private static string _pageSlug = string.Empty;

    [Test]
    public async Task Home_LoadsNoMudBlazor() => await AssertNoMudBlazorAsync("/");

    [Test]
    public async Task Post_LoadsNoMudBlazor()
    {
        await EnsureFixturesAsync();
        await AssertNoMudBlazorAsync($"/post/{_postSlug}");
    }

    [Test]
    public async Task Category_LoadsNoMudBlazor()
    {
        await EnsureFixturesAsync();
        await AssertNoMudBlazorAsync($"/category/{_categorySlug}");
    }

    [Test]
    public async Task Tag_LoadsNoMudBlazor()
    {
        await EnsureFixturesAsync();
        await AssertNoMudBlazorAsync($"/tag/{_tagSlug}");
    }

    [Test]
    public async Task Categories_LoadsNoMudBlazor()
    {
        await EnsureFixturesAsync();
        await AssertNoMudBlazorAsync("/categories");
    }

    [Test]
    public async Task StaticPage_LoadsNoMudBlazor()
    {
        await EnsureFixturesAsync();
        await AssertNoMudBlazorAsync($"/{_pageSlug}");
    }

    [Test]
    public async Task Search_LoadsNoMudBlazor()
    {
        await EnsureFixturesAsync();
        await AssertNoMudBlazorAsync($"/search?q={Uri.EscapeDataString(RunId)}");
    }

    [Test]
    public async Task SearchWithoutAQuery_LoadsNoMudBlazor() => await AssertNoMudBlazorAsync("/search");

    [Test]
    public async Task AdminPage_StillLoadsMudBlazor_AndRendersItsComponents()
    {
        await LoginAsync("admin@example.com", "password");
        var requests = TrackRequests(Page);

        var response = await Page.GotoAsync($"{BaseUrl}/admin/posts");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        AssertMudBlazorLoaded(requests, "/admin/posts");
        Assert.That(await response!.TextAsync(), Does.Contain("mud-"), "an admin document renders MudBlazor markup.");
        await Expect(Page.Locator("main .mud-table, main .mud-button-root, .mud-popover-provider").First)
            .ToBeAttachedAsync();
        // MudThemeProvider writes its palette variables: the stylesheet really is in force.
        Assert.That(await Page.EvaluateAsync<string>(
            "() => getComputedStyle(document.documentElement).getPropertyValue('--mud-palette-primary')"),
            Is.Not.Empty, "the admin page carries MudThemeProvider's palette.");
    }

    [Test]
    public async Task NotFoundPage_LoadsNoMudBlazor_AndDrawsItsLinks()
    {
        // Brief 13a: the not-found page is a static document like the reading pages.
        var requests = TrackRequests(Page);

        var response = await Page.GotoAsync($"{BaseUrl}/not-found");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        Assert.That(response!.Status, Is.EqualTo(200));
        Assert.That(await response.TextAsync(), Does.Not.Contain("mud-"), "/not-found carries no MudBlazor class or variable.");
        Assert.That(requests.Where(IsMudBlazorAsset), Is.Empty, "/not-found downloads no MudBlazor assets.");
        await Expect(Page.Locator("main a.not-found__link")).ToHaveCountAsync(2);
    }

    [TestCase("/post/missing-post")]
    [TestCase("/category/missing-category")]
    [TestCase("/tag/missing-tag")]
    [TestCase("/missing-page")]
    public async Task MissingItem_NotFoundDocument_LoadsNoMudBlazor_AndLooksLikeTheNotFoundPage(string path)
    {
        // A reading page that calls NotFound() renders the not-found copy inside its own static
        // document: no MudBlazor there either, so the copy is plain markup that must still read
        // as the not-found page (a 3rem heading, upper-case accent-coloured links).
        var requests = TrackRequests(Page);

        var response = await Page.GotoAsync($"{BaseUrl}{path}-{RunId}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        Assert.That(response!.Status, Is.EqualTo(404), $"{path} is missing: a real 404.");
        var html = await response.TextAsync();
        Assert.That(html, Does.Contain("This page has wandered off"), "the not-found copy renders.");
        Assert.That(html, Does.Not.Contain("mud-"), $"{path}: the static 404 document carries a MudBlazor class or variable.");
        Assert.That(requests.Where(IsMudBlazorAsset), Is.Empty,
            $"{path}: the static 404 document downloaded MudBlazor assets.");
        Assert.That(await Page.Locator("main h1").EvaluateAsync<string>("el => getComputedStyle(el).fontSize"),
            Is.EqualTo("48px"), "the heading is drawn by the page's own stylesheet.");
        var links = Page.Locator("main a.not-found__link");
        await Expect(links).ToHaveCountAsync(2);
        Assert.That(await links.First.EvaluateAsync<string>("el => getComputedStyle(el).textTransform"),
            Is.EqualTo("uppercase"), "the links are drawn as text buttons.");
    }

    [Test]
    public async Task UnknownRoute_NotFoundDocument_IsStatic_NoCircuit_StaticPolicy_AndLoadsNoMudBlazor()
    {
        // Brief 13a: the catch-all page is a static endpoint. The anonymous visitor gets the real
        // 404 with the static policy (no MudBlazor script sources), no MudBlazor and no circuit.
        var requests = TrackRequests(Page);
        var circuitTraffic = new List<string>();
        Page.WebSocket += (_, socket) => { lock (circuitTraffic) { circuitTraffic.Add(socket.Url); } };
        Page.Request += (_, request) =>
        {
            if (request.Url.Contains("/_blazor", StringComparison.Ordinal))
            {
                lock (circuitTraffic) { circuitTraffic.Add(request.Url); }
            }
        };

        var response = await Page.GotoAsync($"{BaseUrl}/no/such/route/{RunId}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        Assert.That(response!.Status, Is.EqualTo(404));
        var policy = await response.HeaderValueAsync("content-security-policy")
            ?? await response.HeaderValueAsync("content-security-policy-report-only");
        Assert.That(policy, Is.Not.Null.And.Not.Contain("/_content/MudBlazor/"),
            "the 404 document carries the static policy, which names no MudBlazor script source.");
        Assert.That(await response.TextAsync(), Does.Not.Contain("mud-"));
        Assert.That(requests.Where(IsMudBlazorAsset), Is.Empty);
        await Expect(Page.Locator("main a.not-found__link")).ToHaveCountAsync(2);
        Assert.That(circuitTraffic, Is.Empty, "an anonymous visitor on a 404 opens no circuit.");
    }

    private async Task AssertNoMudBlazorAsync(string path)
    {
        var requests = TrackRequests(Page);

        var response = await Page.GotoAsync($"{BaseUrl}{path}");
        Assert.That(response!.Status, Is.EqualTo(200), $"{path} must render.");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var html = await response.TextAsync();
        Assert.That(html, Does.Not.Contain("mud-"), $"{path}: the HTML carries a MudBlazor class or variable.");
        Assert.That(html, Does.Not.Contain("MudBlazor"), $"{path}: the HTML refers to MudBlazor.");
        Assert.That(requests.Where(IsMudBlazorAsset), Is.Empty,
            $"{path} downloaded MudBlazor assets: {string.Join(", ", requests.Where(IsMudBlazorAsset))}.");
        Assert.That(requests, Has.Some.Matches<string>(url => url!.Contains("/js/static-glue")),
            "the page's own script did load (the log is live).");
    }

    private static void AssertMudBlazorLoaded(IReadOnlyCollection<string> requests, string path)
    {
        Assert.That(requests.Where(url => url.Contains("MudBlazor.min") && url.Contains(".css")), Is.Not.Empty,
            $"{path} (an interactive document) must load MudBlazor's stylesheet.");
        Assert.That(requests.Where(url => url.Contains("MudBlazor.min") && url.Contains(".js")), Is.Not.Empty,
            $"{path} (an interactive document) must load MudBlazor's script.");
    }

    private static bool IsMudBlazorAsset(string url) => url.Contains("MudBlazor", StringComparison.OrdinalIgnoreCase);

    /// <summary>The URLs the page requests, as the browser makes them (Playwright's request log).</summary>
    private static List<string> TrackRequests(IPage page)
    {
        var urls = new List<string>();
        page.Request += (_, request) =>
        {
            lock (urls)
            {
                urls.Add(request.Url);
            }
        };
        return urls;
    }

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

            var category = await CreateCategoryAsync($"Plain Cat {RunId}");
            _categorySlug = category.Slug;
            _tagSlug = $"plain{RunId}";
            var post = await PublishAsync($"Plain Post {RunId}", BodyWithSections(RunId, 3), [_tagSlug], category.Id);
            _postSlug = post.Slug;
            var page = await PublishAsync($"Plain About {RunId}", $"Pagemarker {RunId} waits.", kind: PostKind.Page);
            _pageSlug = page.Slug;
            _dataReady = true;
        }
        finally
        {
            DataGate.Release();
        }
    }
}
