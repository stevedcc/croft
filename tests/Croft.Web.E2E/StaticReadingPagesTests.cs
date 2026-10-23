using Croft.Shared.Enums;

namespace Croft.Web.E2E;

/// <summary>
/// Brief 09f item 1: the seven reading pages (Home, Post, Category, Tag, Categories, StaticPage,
/// Search) render as static SSR — no Blazor circuit (no WebSocket to /_blazor) and no
/// <c>&lt;!--Blazor:</c> server marker in the HTML — while admin pages still connect a circuit.
/// Crossing between the two areas is always a full document load (the CSP is fixed per document,
/// see AdminArea): static → admin starts a circuit, admin → a reading page loads the reading page
/// statically again. The outcomes asserted: the WebSocket events the browser actually saw, the
/// markers in the raw HTML response, and the page content actually showing.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class StaticReadingPagesTests : StaticReadingTest
{
    private static readonly SemaphoreSlim DataGate = new(1, 1);
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];

    private static bool _dataReady;
    private static string _postSlug = string.Empty;
    private static string _postTitle = string.Empty;
    private static string _categorySlug = string.Empty;
    private static string _tagSlug = string.Empty;
    private static string _pageSlug = string.Empty;

    [Test]
    public async Task Home_OpensNoCircuit() => await AssertStaticAsync("/", "h1, .post-list, .post-card");

    [Test]
    public async Task Post_OpensNoCircuit()
    {
        await EnsureFixturesAsync();
        await AssertStaticAsync($"/post/{_postSlug}", ".markdown-body", expectedText: _postTitle);
    }

    [Test]
    public async Task Category_OpensNoCircuit()
    {
        await EnsureFixturesAsync();
        await AssertStaticAsync($"/category/{_categorySlug}", ".post-list", expectedText: _postTitle);
    }

    [Test]
    public async Task Tag_OpensNoCircuit()
    {
        await EnsureFixturesAsync();
        await AssertStaticAsync($"/tag/{_tagSlug}", ".post-list", expectedText: _postTitle);
    }

    [Test]
    public async Task Categories_OpensNoCircuit()
    {
        await EnsureFixturesAsync();
        await AssertStaticAsync("/categories", "main");
    }

    [Test]
    public async Task StaticPage_OpensNoCircuit()
    {
        await EnsureFixturesAsync();
        await AssertStaticAsync($"/{_pageSlug}", ".markdown-body", expectedText: $"Pagemarker {RunId}");
    }

    [Test]
    public async Task Search_OpensNoCircuit()
    {
        await EnsureFixturesAsync();
        await AssertStaticAsync($"/search?q={Uri.EscapeDataString(RunId)}", ".post-list", expectedText: _postTitle);
    }

    [Test]
    public async Task AdminPage_StillConnectsACircuit_AndRendersTheServerMarker()
    {
        await LoginAsync("admin@example.com", "password");

        var sockets = TrackWebSockets(Page);
        var response = await Page.GotoAsync($"{BaseUrl}/admin/posts");
        Assert.That(await response!.TextAsync(), Does.Contain("<!--Blazor:"),
            "an admin page prerenders for an interactive circuit: the server marker must be present.");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Posts" }).First).ToBeVisibleAsync();
        await WaitForCircuitSocketAsync(sockets);
    }

    [Test]
    public async Task Navigation_StaticToAdmin_StartsACircuit_AndAdminToReadingPage_IsAFullLoadOfAStaticPage()
    {
        await LoginAsync("admin@example.com", "password");

        // A reading page, logged in as admin: static, no circuit. Mark the document.
        var sockets = TrackWebSockets(Page);
        await Page.GotoAsync(BaseUrl);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Page.EvaluateAsync("() => { window.__docMarker = 'reading'; }");
        Assert.That(sockets.Urls, Is.Empty, "the reading page must not have opened a circuit.");

        // Static → admin by link click: crosses the area boundary, so a fresh document loads and
        // its circuit connects.
        await Page.Locator("#sidebar-nav a[href='/admin/posts']").ClickAsync();
        await Page.WaitForURLAsync(url => url.EndsWith("/admin/posts"));
        await WaitForCircuitSocketAsync(sockets);
        Assert.That(await Page.EvaluateAsync<string?>("() => window.__docMarker"), Is.Null,
            "static → admin loads a fresh document (the CSP is fixed per document).");

        // Admin → reading page: a full load again (marker gone), showing the reading page, with
        // no new circuit.
        await Page.EvaluateAsync("() => { window.__docMarker = 'admin'; }");
        var socketsBefore = sockets.Count;
        await Page.Locator("#sidebar-nav a[href='/search']").ClickAsync();
        await Page.WaitForURLAsync(url => url.EndsWith("/search"));
        await Expect(Page.Locator("#search-field")).ToBeVisibleAsync();
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        Assert.That(await Page.EvaluateAsync<string?>("() => window.__docMarker"), Is.Null,
            "admin → a reading page loads a fresh document (full load).");
        Assert.That(sockets.Count, Is.EqualTo(socketsBefore),
            "the reading page the admin area navigated to must not open another circuit.");
    }

    /// <summary>Opens the path in a fresh page and asserts: no WebSocket, no server marker in the
    /// raw HTML, and the page's real content showing.</summary>
    private async Task AssertStaticAsync(string path, string contentSelector, string? expectedText = null)
    {
        var sockets = TrackWebSockets(Page);
        var response = await Page.GotoAsync($"{BaseUrl}{path}");
        Assert.That(response!.Status, Is.EqualTo(200), $"{path} must render.");
        Assert.That(await response.TextAsync(), Does.Not.Contain("<!--Blazor:"),
            $"{path} is static SSR: its HTML must carry no Blazor server marker.");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.Locator(contentSelector).First).ToBeVisibleAsync();
        if (expectedText is not null)
        {
            await Expect(Page.Locator("main")).ToContainTextAsync(expectedText);
        }

        Assert.That(sockets.Urls, Is.Empty, $"{path} must open no WebSocket, but opened: {string.Join(", ", sockets.Urls)}.");
    }

    private static SocketLog TrackWebSockets(IPage page)
    {
        var log = new SocketLog();
        page.WebSocket += (_, socket) => log.Add(socket.Url);
        return log;
    }

    private static async Task WaitForCircuitSocketAsync(SocketLog sockets)
    {
        try
        {
            await sockets.CircuitOpened.WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (TimeoutException)
        {
            Assert.Fail($"no WebSocket to /_blazor was opened within 15 s (saw: {string.Join(", ", sockets.Urls)}).");
        }
    }

    /// <summary>The WebSockets a page opened; <see cref="CircuitOpened"/> completes on the first
    /// one to /_blazor.</summary>
    private sealed class SocketLog
    {
        private readonly List<string> _urls = [];
        private readonly TaskCompletionSource _circuitOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task CircuitOpened => _circuitOpened.Task;

        public int Count
        {
            get { lock (_urls) { return _urls.Count; } }
        }

        public IReadOnlyList<string> Urls
        {
            get { lock (_urls) { return [.. _urls]; } }
        }

        public void Add(string url)
        {
            lock (_urls)
            {
                _urls.Add(url);
            }

            if (url.Contains("/_blazor"))
            {
                _circuitOpened.TrySetResult();
            }
        }
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

            var category = await CreateCategoryAsync($"Static Cat {RunId}");
            _categorySlug = category.Slug;
            _tagSlug = $"static{RunId}";
            _postTitle = $"Static Post {RunId}";
            var post = await PublishAsync(_postTitle, BodyWithSections(RunId, 3), [_tagSlug], category.Id);
            _postSlug = post.Slug;
            var page = await PublishAsync($"Static About {RunId}", $"Pagemarker {RunId} waits.", kind: PostKind.Page);
            _pageSlug = page.Slug;
            _dataReady = true;
        }
        finally
        {
            DataGate.Release();
        }
    }
}
