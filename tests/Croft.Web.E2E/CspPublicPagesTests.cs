using Croft.Shared.Enums;

namespace Croft.Web.E2E;

/// <summary>
/// Brief 10c item 2: the public side of the site under the enforced deny-by-default policy
/// (<c>default-src 'none'</c> with every directive written out), measured in whichever engine the
/// run selects (<c>BROWSER=chromium|firefox|webkit</c>; dev/run-e2e.sh --browser). Every document
/// kind an anonymous visitor can open loads with ZERO CSP violations: the seven reading pages and
/// their not-found documents, enhanced navigation between them (with the page-view beacon), the
/// fonts, the icons and the manifest, and the static not-found page, which opens no circuit. Fixtures go through the API, not the sign-in UI,
/// because WebKit cannot sign in over http://localhost (its OIDC correlation cookie is Secure);
/// the staff documents are measured by <see cref="CspTests"/>, which skips WebKit for that reason.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
[Category("Csp")]
public class CspPublicPagesTests : StaticReadingTest
{
    private static readonly SemaphoreSlim DataGate = new(1, 1);
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];

    private static bool _dataReady;
    private static string _bannerSlug = string.Empty;
    private static string _plainSlug = string.Empty;
    private static string _categorySlug = string.Empty;
    private static string _tagSlug = string.Empty;
    private static string _pageSlug = string.Empty;

    private static string Engine => Environment.GetEnvironmentVariable("BROWSER") is { Length: > 0 } browser ? browser : "chromium";

    [Test]
    public void TheEngineUnderTest_IsTheOneTheRunAskedFor() =>
        Assert.That(Browser.BrowserType.Name, Is.EqualTo(Engine),
            "a run for another engine would measure the wrong browser");

    [Test]
    public async Task ReadingPages_LoadUnderTheEnforcedPolicy_ZeroViolations()
    {
        await EnsureFixturesAsync();
        var probe = await CspViolationProbe.AttachAsync(Page);

        await probe.StepAsync("home");
        await Page.GotoAsync(BaseUrl);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await WaitForInteractiveAsync(Page);
        await Expect(Page.Locator(".post-card").First).ToBeVisibleAsync();

        await probe.StepAsync("post with banner and code");
        await Page.GotoAsync($"{BaseUrl}/post/{_bannerSlug}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await WaitForInteractiveAsync(Page);
        await AssertImageLoadsAsync(Page.Locator(".post-hero__media .banner-or-fallback__img"));
        Assert.That(await Page.Locator("pre code.hljs").CountAsync(), Is.GreaterThan(0),
            "the highlighter ran: the post's scripts were allowed to execute");

        await probe.StepAsync("post with fallback banner");
        await Page.GotoAsync($"{BaseUrl}/post/{_plainSlug}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.Locator(".post-hero__media .banner-or-fallback__fallback")).ToBeVisibleAsync();

        await probe.StepAsync("static page");
        await Page.GotoAsync($"{BaseUrl}/{_pageSlug}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.Locator("main")).ToContainTextAsync($"Pagemarker {RunId}");

        await probe.StepAsync("category");
        await Page.GotoAsync($"{BaseUrl}/category/{_categorySlug}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.Locator(".post-card").First).ToBeVisibleAsync();

        await probe.StepAsync("tag");
        await Page.GotoAsync($"{BaseUrl}/tag/{_tagSlug}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.Locator(".post-card").First).ToBeVisibleAsync();

        await probe.StepAsync("search with a typed query");
        await Page.GotoAsync($"{BaseUrl}/search");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await WaitForInteractiveAsync(Page);
        await Page.GetByRole(AriaRole.Searchbox, new PageGetByRoleOptions { Name = "Search" }).FillAsync(RunId);
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Search" }).ClickAsync();
        await Expect(Page.GetByText($"results for '{RunId}'")).ToBeVisibleAsync();
        await Expect(Page.Locator(".post-card").First).ToBeVisibleAsync();

        await probe.StepAsync("not-found document of a reading page");
        var missing = await Page.GotoAsync($"{BaseUrl}/post/no-such-post-{RunId}");
        Assert.That(missing!.Status, Is.EqualTo(404));
        await Expect(Page.GetByText("This page has wandered off")).ToBeVisibleAsync();

        await AssertZeroViolationsAsync(probe, $"reading pages in {Engine}");
    }

    [Test]
    public async Task EnhancedNavigation_BetweenReadingPages_SendsTheBeacon_ZeroViolations()
    {
        await EnsureFixturesAsync();
        var probe = await CspViolationProbe.AttachAsync(Page);

        await probe.StepAsync("home");
        await Page.GotoAsync(BaseUrl);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await WaitForInteractiveAsync(Page);
        await Page.EvaluateAsync("() => { window.__cspNavMarker = 'same document'; }");

        await probe.StepAsync("enhanced navigation to a post, beacon sent");
        var beacon = Page.WaitForResponseAsync(r =>
            r.Url.EndsWith("/api/analytics/pageview") && r.Request.Method == "POST");
        await Page.Locator($"a.post-card__link[href='/post/{_bannerSlug}'], a[href='/post/{_bannerSlug}']").First.ClickAsync();
        await Page.WaitForURLAsync(url => url.EndsWith($"/post/{_bannerSlug}"));
        await WaitForInteractiveAsync(Page);
        var beaconResponse = await beacon;
        Assert.That(beaconResponse.Ok, Is.True, $"the page-view beacon must be accepted; got {beaconResponse.Status}.");
        await Expect(Page.Locator("pre code.hljs").First).ToBeAttachedAsync();

        await probe.StepAsync("enhanced navigation to a category");
        await Page.Locator("#sidebar-nav a[href^='/category/']").First.ClickAsync();
        await Page.WaitForURLAsync(url => url.Contains("/category/"));
        await WaitForInteractiveAsync(Page);
        await Expect(Page.Locator(".post-card").First).ToBeVisibleAsync();

        Assert.That(await Page.EvaluateAsync<string?>("() => window.__cspNavMarker ?? null"), Is.EqualTo("same document"),
            "the navigations must have been enhanced (one document), the path the policy has to carry");
        await AssertZeroViolationsAsync(probe, $"enhanced navigation in {Engine}");
    }

    [Test]
    public async Task NotFoundPage_IsAStaticDocument_OpensNoCircuit_ZeroViolations()
    {
        // Brief 13a: the not-found page is a static document; an anonymous visitor who lands on
        // it opens no circuit (no WebSocket, no /_blazor request) and the static policy holds.
        var probe = await CspViolationProbe.AttachAsync(Page);
        var circuitTraffic = new List<string>();
        Page.WebSocket += (_, socket) => { lock (circuitTraffic) { circuitTraffic.Add(socket.Url); } };
        Page.Request += (_, request) =>
        {
            if (request.Url.Contains("/_blazor", StringComparison.Ordinal))
            {
                lock (circuitTraffic) { circuitTraffic.Add(request.Url); }
            }
        };

        await probe.StepAsync("static not-found page");
        var response = await Page.GotoAsync($"{BaseUrl}/not-found");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await WaitForInteractiveAsync(Page);

        Assert.That(response!.Status, Is.EqualTo(200));
        await Expect(Page.GetByText("This page has wandered off")).ToBeVisibleAsync();
        Assert.That(circuitTraffic, Is.Empty, $"a static not-found page must not ask for a circuit in {Engine}");
        await AssertZeroViolationsAsync(probe, $"not-found page in {Engine}");
    }

    [Test]
    public async Task FontsAndIcons_LoadUnderTheEnforcedPolicy()
    {
        var probe = await CspViolationProbe.AttachAsync(Page);
        var fontResponses = new List<(string Url, int Status)>();
        Page.Response += (_, response) =>
        {
            if (response.Url.Contains(".woff2", StringComparison.Ordinal))
            {
                lock (fontResponses) { fontResponses.Add((response.Url, response.Status)); }
            }
        };

        await probe.StepAsync("home, fonts and icons");
        await Page.GotoAsync(BaseUrl);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Page.EvaluateAsync("() => document.fonts.ready");

        Assert.That(fontResponses, Is.Not.Empty, "the preloaded fonts were requested");
        Assert.That(fontResponses.Where(f => f.Status != 200), Is.Empty, "every font request was answered");
        Assert.That(await Page.EvaluateAsync<int>("() => [...document.fonts].filter(f => f.status === 'loaded').length"),
            Is.GreaterThan(0), "the browser applied at least one font face");

        // A favicon is an image request governed by img-src; load each icon the page names the way
        // the browser would, and require that it decoded.
        var icons = await Page.EvaluateAsync<string[]>(
            "() => [...document.querySelectorAll('link[rel~=icon], link[rel=apple-touch-icon]')].map(l => l.href)");
        Assert.That(icons, Is.Not.Empty, "the page names its icons");
        foreach (var icon in icons)
        {
            var decoded = await Page.EvaluateAsync<bool>(
                "async href => { const img = new Image(); img.src = href; try { await img.decode(); return true; } catch { return false; } }",
                icon);
            Assert.That(decoded, Is.True, $"the icon {icon} must load under img-src");
        }

        await AssertZeroViolationsAsync(probe, $"fonts and icons in {Engine}");
    }

    [Test]
    public async Task Manifest_IsFetchedAndParsed_UnderManifestSrc()
    {
        if (Engine != "chromium")
        {
            // Only Chromium exposes a way to make the browser fetch the manifest on demand
            // (DevTools Page.getAppManifest); Firefox and WebKit fetch it only for an install
            // prompt that automation does not trigger. The policy text is pinned by the snapshots.
            Assert.Ignore($"{Engine} offers no on-demand manifest fetch in automation.");
        }

        await Page.GotoAsync(BaseUrl);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        var session = await Context.NewCDPSessionAsync(Page);
        var manifest = await session.SendAsync("Page.getAppManifest");
        Assert.That(manifest, Is.Not.Null);
        var root = manifest!.Value;
        var errors = root.GetProperty("errors").EnumerateArray()
            .Select(e => e.GetProperty("message").GetString()).ToList();
        Assert.That(root.GetProperty("url").GetString(), Does.EndWith("manifest.webmanifest"));
        Assert.That(errors, Is.Empty, $"the manifest must be fetched and parsed under manifest-src; errors: {string.Join("; ", errors)}");
        Assert.That(root.GetProperty("data").GetString(), Does.Contain("\"short_name\""),
            "the manifest body was delivered, not just its URL");
    }

    private static async Task AssertZeroViolationsAsync(CspViolationProbe probe, string surface)
    {
        await probe.DrainAsync();
        var findings = CspViolationProbe.FormatFindings(probe.Violations, probe.ConsoleErrors);
        TestContext.Progress.WriteLine($"CSP measurement ({surface}): {findings}");
        Assert.That(probe.Violations, Has.Count.EqualTo(0), findings);
        Assert.That(probe.ConsoleErrors, Has.Count.EqualTo(0), findings);
        Assert.That(probe.TrustedTypesReports.Where(report => !report.SourceFile.Contains("blazor.web", StringComparison.Ordinal)), Is.Empty,
            "only blazor.web.js (W-091) may report a Trusted Types violation; none of the site's own scripts does");
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

            var category = await CreateCategoryAsync($"Csp Pub Cat {RunId}");
            _categorySlug = category.Slug;
            _tagSlug = $"csppub{RunId}";
            var banner = await UploadBannerAsync($"csp-pub-{RunId}.png");
            var withBanner = await PublishAsync($"Csp Pub Banner {RunId}", BodyWithSections(RunId, 3), [_tagSlug], category.Id, bannerImageId: banner);
            _bannerSlug = withBanner.Slug;
            // No category: KeyboardFocusTests tabs through the home page's cards and reads a repeated
            // control (two cards with the same category chip) as the focus wrapping around.
            var plain = await PublishAsync($"Csp Pub Plain {RunId}", BodyWithSections(RunId, 2));
            _plainSlug = plain.Slug;
            var page = await PublishAsync($"Csp Pub About {RunId}", $"Pagemarker {RunId} waits.", kind: PostKind.Page);
            _pageSlug = page.Slug;
            _dataReady = true;
        }
        finally
        {
            DataGate.Release();
        }
    }
}
