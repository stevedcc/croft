using System.Text;
using System.Text.Json;

namespace Croft.Web.E2E;

/// <summary>
/// The CSP measurement: every page surface driven under the ENFORCED policy in light and dark,
/// with every <c>securitypolicyviolation</c> event and console CSP error recorded by
/// <see cref="CspViolationProbe"/>. The assertion is ZERO violations; the failure message is the
/// full finding list (every violation with directive, blocked URI and sample, then the same
/// grouped by what it is), which is the ordered work list for the policy's allow-lists. The
/// allow-lists landed with the policy's contributors (the inline theme bootstrap script,
/// MudBlazor's style elements and the staff area's style attributes), so the suite is green.
/// The mobile contents panel joins the matrix on the post page at 390x844 (at rest, pinned and
/// opened): its states are sized only through the CSSOM, which the public policy allows and the
/// contents-panel measurement keeps pinned down per state.
///
/// Category=Csp. The measurement runs against the ENFORCED policy, which is what the app
/// serves by default (Csp:ReportOnly defaults to false), so it runs in the normal E2E run like
/// everything else — dev/run-e2e.sh --csp-enforced narrows to this category on explicitly
/// enforced instances. The <see cref="E2EStack_ServesTheEnforcedPolicy_NotReportOnly"/> guard
/// pins the wiring: a report-only stack fires the same events while blocking nothing and would
/// measure nothing.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
[Category("Csp")]
public class CspTests : WebTest
{
    private static readonly SemaphoreSlim DataGate = new(1, 1);
    private static readonly string RunId = Guid.NewGuid().ToString("N");

    private const string QuotedWord = "hillsideword";

    private static bool _dataReady;
    private static Guid _bannerId;
    private static string _bannerSlug = string.Empty;
    private static string _plainSlug = string.Empty;
    private static string _panelSlug = string.Empty;
    private static string _draftSlug = string.Empty;
    private static string _categorySlug = string.Empty;
    private static string _tagSlug = string.Empty;
    private static string _staticPageSlug = string.Empty;

    private static string CategoryName => $"Csp Cat {RunId}";
    private static string TagName => $"csptag{RunId}";
    private static string BannerTitle => $"Csp Banner {RunId}";
    private static string PlainTitle => $"Csp Plain {RunId}";
    private static string PanelTitle => $"Csp Panel {RunId}";
    private static string DraftTitle => $"Csp Draft {RunId}";
    private static string StaticPageTitle => $"Csp Page {RunId}";

    /// <summary>The measurement only means something against the enforced policy: a report-only
    /// stack would fire the same events while blocking nothing, so the wiring is pinned here.</summary>
    [Test]
    public async Task E2EStack_ServesTheEnforcedPolicy_NotReportOnly()
    {
        await using var response = await Page.APIRequest.GetAsync(BaseUrl);
        Assert.That(response.Status, Is.EqualTo(200), $"The Web root must answer 200 on {BaseUrl}.");
        var headerNames = response.Headers.Keys.ToList();
        Assert.Multiple(() =>
        {
            Assert.That(
                headerNames.Any(name => name.Equals("content-security-policy", StringComparison.OrdinalIgnoreCase)),
                Is.True,
                $"The E2E stack must send the enforcing Content-Security-Policy header (Csp:ReportOnly=false); got headers: {string.Join(", ", headerNames)}");
            // The reading pages carry one report-only policy of their own, the Trusted Types
            // directives (W-091); a stack that ran the whole policy report-only would show more.
            Assert.That(
                response.Headers.TryGetValue("content-security-policy-report-only", out var reportOnly) ? reportOnly : string.Empty,
                Is.Empty.Or.EqualTo("require-trusted-types-for 'script'; trusted-types 'none'; report-uri /csp-report; report-to csp"),
                "The E2E stack must not run report-only: the measurement would report violations while nothing is actually blocked.");
        });
    }

    /// <summary>
    /// The policy is fixed per document (enhanced navigation keeps the first document's policy),
    /// and the two areas carry different ones — so every navigation between the public pages and
    /// the staff pages (/admin, /editor) is a full page load: the document is replaced (a marker
    /// set on window before the click is gone) and the response that created the document carries
    /// that area's policy. From home through the admin link, back to a post through its View
    /// link, both ways asserted.
    /// </summary>
    [Test]
    public async Task AreaBoundary_Crossings_LoadANewDocument_WithThatAreasPolicy()
    {
        IgnoreWhereSigningInIsImpossible();
        await EnsureFixturesAsync();
        await LoginAsync("admin@example.com", "password");

        // Public → staff: the sidebar's admin link from home.
        await Page.GotoAsync(BaseUrl);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await WaitForInteractiveAsync(Page);
        await Page.EvaluateAsync("() => { window.__cspBoundaryMarker = 'set'; }");
        var adminDocument = Page.WaitForResponseAsync(
            r => r.Request.ResourceType == "document" && r.Url.StartsWith($"{BaseUrl}/admin"),
            new PageWaitForResponseOptions { Timeout = 15000 });
        await Page.Locator("#sidebar-nav")
            .GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = "Posts", Exact = true }).ClickAsync();
        var adminResponse = await adminDocument;
        await Page.WaitForURLAsync(url => url.StartsWith($"{BaseUrl}/admin"), new PageWaitForURLOptions { Timeout = 15000 });

        Assert.That(await Page.EvaluateAsync<string>("() => window.__cspBoundaryMarker ?? null"), Is.Null,
            "the admin page must be a fresh document: the window it creates carries no marker from home");
        var adminPolicy = await PolicyHeaderAsync(adminResponse);
        Assert.That(adminPolicy, Does.Contain("style-src-attr 'unsafe-inline'"),
            $"the response that created the admin document must carry the admin policy; got: {adminPolicy}");

        // Staff → public: the post's View link from the admin posts list (any published post's).
        await Page.EvaluateAsync("() => { window.__cspBoundaryMarker = 'set'; }");
        var postDocument = Page.WaitForResponseAsync(
            r => r.Request.ResourceType == "document" && r.Url.Contains("/post/"),
            new PageWaitForResponseOptions { Timeout = 15000 });
        await Page.Locator("a.posts-grid__view[href^='/post/']").First.ClickAsync();
        var postResponse = await postDocument;
        await Page.WaitForURLAsync(url => url.Contains("/post/"), new PageWaitForURLOptions { Timeout = 15000 });

        Assert.That(await Page.EvaluateAsync<string>("() => window.__cspBoundaryMarker ?? null"), Is.Null,
            "the post page must be a fresh document: the window it creates carries no marker from the admin area");
        var publicPolicy = await PolicyHeaderAsync(postResponse);
        Assert.Multiple(() =>
        {
            Assert.That(publicPolicy, Does.Contain("style-src-attr 'none'"),
                $"the response that created the public document must carry the public policy; got: {publicPolicy}");
            Assert.That(publicPolicy, Does.Not.Contain("style-src-attr 'unsafe-inline'"),
                "the public document's policy must not leak the staff area's allowance");
        });
    }

    /// <summary>Captures every WebSocket the document opens so a test can drop the live circuit
    /// the way a network blip does, and refuses new ones once <c>window.__circuitDrop</c> is
    /// set, so a reconnection cannot quietly rejoin and change the state under the assertions.</summary>
    private const string CircuitDropInitScript = """
        (() => {
            const Original = window.WebSocket;
            window.__circuitSockets = [];
            window.WebSocket = class extends Original {
                constructor(...args) {
                    if (window.__circuitDrop) {
                        throw new Error('circuit drop simulated: no new connections');
                    }
                    super(...args);
                    window.__circuitSockets.push(this);
                }
            };
        })();
        """;

    /// <summary>
    /// The circuit-drop state the measurement could not see: when the connection to the server
    /// dies (deploy, network blip, laptop wake), the framework shows the reconnect overlay — and
    /// under the enforced policy that overlay must arrive as the styled card the site ships
    /// (its own markup and stylesheet, no framework-inserted inline style element) with ZERO
    /// violations. The circuit is dropped by closing the page's WebSockets and going offline;
    /// the assertions run in that state, on the outcome the user sees — a visible, styled card
    /// over a dimmed page — not on markup being merely present.
    /// </summary>
    [Test]
    public async Task EnforcedCsp_CircuitDrop_ReconnectOverlayStaysStyled_ZeroViolations()
    {
        await LoginAsync("admin@example.com", "password");
        var probe = await CspViolationProbe.AttachAsync(Page);
        await Page.AddInitScriptAsync(CircuitDropInitScript);
        // The reading pages, the not-found and the error pages are static SSR and hold no circuit
        // to drop (brief 13a: circuits are for signed-in staff only), so the drop is measured on
        // an admin page, under the staff policy.
        await Page.GotoAsync($"{BaseUrl}/admin/posts");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await WaitForInteractiveAsync(Page);
        await Page.WaitForFunctionAsync("() => (window.__circuitSockets ?? []).length > 0");

        await probe.StepAsync("circuit drop with the reconnect overlay up");
        await Page.Context.SetOfflineAsync(true);
        await Page.EvaluateAsync(
            "() => { window.__circuitDrop = true; window.__circuitSockets.forEach(socket => socket.close()); }");

        var overlay = Page.Locator("#components-reconnect-modal");
        await overlay.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15000 });
        await Expect(overlay).ToContainTextAsync("Rejoin");

        var card = await Page.EvaluateAsync<JsonElement>(
            """
            () => {
                const el = document.querySelector('#components-reconnect-modal');
                const style = getComputedStyle(el);
                return {
                    shadow: style.boxShadow,
                    radius: parseFloat(style.borderRadius),
                    width: el.offsetWidth,
                    rem: parseFloat(getComputedStyle(document.documentElement).fontSize),
                    backdrop: getComputedStyle(el, '::backdrop').backgroundColor,
                };
            }
            """);

        await probe.DrainAsync();
        var findings = CspViolationProbe.FormatFindings(probe.Violations, probe.ConsoleErrors);
        TestContext.Progress.WriteLine($"CSP measurement (circuit drop, reconnect overlay up): {findings}");
        Assert.Multiple(() =>
        {
            Assert.That(card.GetProperty("width").GetDouble(), Is.EqualTo(20 * card.GetProperty("rem").GetDouble()).Within(1),
                "the overlay must be the 20rem reconnect card, not an unpositioned blob of status text");
            Assert.That(card.GetProperty("radius").GetDouble(), Is.EqualTo(0.5 * card.GetProperty("rem").GetDouble()).Within(1),
                "the card must be rounded like the default reconnect look");
            Assert.That(card.GetProperty("shadow").GetString(), Is.Not.Empty.And.Not.EqualTo("none"),
                "the card must be lifted off the dimmed page like the pre-branch default look");
            Assert.That(card.GetProperty("backdrop").GetString(), Is.EqualTo("rgba(0, 0, 0, 0.4)"),
                "the page behind the card must be dimmed like the pre-branch default look");
            Assert.That(probe.Violations, Has.Count.EqualTo(0),
                $"a circuit drop must produce zero CSP violations; findings: {findings}");
            Assert.That(probe.ConsoleErrors, Has.Count.EqualTo(0),
                $"a circuit drop must produce zero CSP console errors; findings: {findings}");
        });
    }

    /// <summary>Its fixtures and the staff pages need a signed-in browser, and WebKit cannot sign
    /// in over http://localhost: the OIDC correlation cookie is SameSite=None; Secure and WebKit
    /// does not treat localhost as a secure origin for it ("Correlation failed" at
    /// /signin-oidc). The anonymous document kinds are measured in every engine by
    /// <see cref="CspPublicPagesTests"/>.</summary>
    private void IgnoreWhereSigningInIsImpossible()
    {
        if (Browser.BrowserType.Name == "webkit")
        {
            Assert.Ignore("WebKit cannot sign in over plain http://localhost (Secure correlation cookie); see CspPublicPagesTests for its coverage.");
        }
    }

    /// <summary>The CSP header of a document response, whichever name the mode gives it.</summary>
    private static async Task<string> PolicyHeaderAsync(IResponse response)
    {
        var headers = await response.AllHeadersAsync();
        Assert.That(
            headers.Keys.Any(name => name.Equals("content-security-policy", StringComparison.OrdinalIgnoreCase)
                || name.Equals("content-security-policy-report-only", StringComparison.OrdinalIgnoreCase)),
            Is.True, $"the document response must carry a CSP header; got: {string.Join(", ", headers.Keys)}");
        return headers.First(pair => pair.Key.Equals("content-security-policy", StringComparison.OrdinalIgnoreCase)
            || pair.Key.Equals("content-security-policy-report-only", StringComparison.OrdinalIgnoreCase)).Value;
    }

    [Test]
    public Task EnforcedCsp_PublicPages_Light_ZeroViolations() => MeasurePublicAsync(ColorScheme.Light);
    [Test]
    public Task EnforcedCsp_PublicPages_Dark_ZeroViolations() => MeasurePublicAsync(ColorScheme.Dark);

    [Test]
    public Task EnforcedCsp_AdminPages_Light_ZeroViolations() => MeasureAdminAsync(ColorScheme.Light);

    [Test]
    public Task EnforcedCsp_AdminPages_Dark_ZeroViolations() => MeasureAdminAsync(ColorScheme.Dark);

    [Test]
    public Task EnforcedCsp_ContentsPanel_Light_ZeroViolations() => MeasureContentsPanelAsync(ColorScheme.Light);

    [Test]
    public Task EnforcedCsp_ContentsPanel_Dark_ZeroViolations() => MeasureContentsPanelAsync(ColorScheme.Dark);

    /// <summary>Home, the two post shapes (banner and fallback banner), a published static page,
    /// category, tag, search with a typed query and the 404 page, each under the emulated scheme,
    /// then one theme-switch toggle on home.</summary>
    private async Task MeasurePublicAsync(ColorScheme scheme)
    {
        IgnoreWhereSigningInIsImpossible();
        await EnsureFixturesAsync();
        var probe = await CspViolationProbe.AttachAsync(Page);
        await Page.EmulateMediaAsync(new PageEmulateMediaOptions { ColorScheme = scheme });
        var theme = scheme == ColorScheme.Dark ? "dark" : "light";

        await probe.StepAsync($"home ({theme})");
        await Page.GotoAsync(BaseUrl);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await WaitForInteractiveAsync(Page);
        await Expect(Page.Locator(".post-card").First).ToBeVisibleAsync();

        await probe.StepAsync($"post with banner ({theme})");
        await Page.GotoAsync($"{BaseUrl}/post/{_bannerSlug}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = BannerTitle, Level = 1 }))
            .ToBeVisibleAsync();
        await AssertImageLoadsAsync(Page.Locator(".post-hero__media .banner-or-fallback__img"));

        await probe.StepAsync($"post with fallback banner ({theme})");
        await Page.GotoAsync($"{BaseUrl}/post/{_plainSlug}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = PlainTitle, Level = 1 }))
            .ToBeVisibleAsync();
        await Expect(Page.Locator(".post-hero__media .banner-or-fallback__fallback")).ToBeVisibleAsync();

        await probe.StepAsync($"static page ({theme})");
        await Page.GotoAsync($"{BaseUrl}/{_staticPageSlug}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = StaticPageTitle, Level = 1 }))
            .ToBeVisibleAsync();

        await probe.StepAsync($"category ({theme})");
        await Page.GotoAsync($"{BaseUrl}/category/{_categorySlug}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.Locator(".post-card").First).ToBeVisibleAsync();

        await probe.StepAsync($"tag ({theme})");
        await Page.GotoAsync($"{BaseUrl}/tag/{_tagSlug}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.Locator(".post-card").First).ToBeVisibleAsync();

        await probe.StepAsync($"search typed query ({theme})");
        await Page.GotoAsync($"{BaseUrl}/search");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await WaitForInteractiveAsync(Page);
        await Page.GetByRole(AriaRole.Searchbox, new PageGetByRoleOptions { Name = "Search" }).FillAsync(RunId);
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Search" }).ClickAsync();
        await Expect(Page.GetByText($"results for '{RunId}'")).ToBeVisibleAsync();
        await Expect(Page.Locator(".post-card").First).ToBeVisibleAsync();

        await probe.StepAsync($"404 page ({theme})");
        await Page.GotoAsync($"{BaseUrl}/post/no-such-post-{RunId}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.GetByText("This page has wandered off")).ToBeVisibleAsync();

        await probe.StepAsync($"theme switch toggle on home ({theme})");
        await Page.GotoAsync(BaseUrl);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await WaitForInteractiveAsync(Page);
        await ToggleThemeSwitchAsync();

        await AssertZeroViolationsAsync(probe, $"public pages, {theme}");
    }

    /// <summary>Everything the signed-in roles see: the admin posts list, tags, media library, the
    /// post editor (new and existing, each with the media picker dialog open), the draft review
    /// page with the selection "Add feedback" button up, and the analytics chart page — then one
    /// theme-switch toggle on the admin posts list. One admin login covers every route: the
    /// review page is Editor,Admin.</summary>
    private async Task MeasureAdminAsync(ColorScheme scheme)
    {
        IgnoreWhereSigningInIsImpossible();
        await EnsureFixturesAsync();
        var probe = await CspViolationProbe.AttachAsync(Page);
        await Page.EmulateMediaAsync(new PageEmulateMediaOptions { ColorScheme = scheme });
        var theme = scheme == ColorScheme.Dark ? "dark" : "light";

        await probe.StepAsync($"login ({theme})");
        await LoginAsync("admin@example.com", "password");

        await probe.StepAsync($"admin posts list ({theme})");
        await Page.GotoAsync($"{BaseUrl}/admin/posts");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Posts", Level = 1 }))
            .ToBeVisibleAsync();
        await Expect(Page.Locator(".posts-grid__row").First).ToBeVisibleAsync();

        await probe.StepAsync($"admin tags page ({theme})");
        await Page.GotoAsync($"{BaseUrl}/admin/tags");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.GetByLabel("New category")).ToBeVisibleAsync();

        await probe.StepAsync($"admin media library ({theme})");
        await Page.GotoAsync($"{BaseUrl}/admin/media");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.Locator(".media-tile").First).ToBeVisibleAsync();

        await probe.StepAsync($"editor new post, media picker open ({theme})");
        await Page.GotoAsync($"{BaseUrl}/admin/posts/new");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await WaitForInteractiveAsync(Page);
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "New Post", Level = 1 }))
            .ToBeVisibleAsync();
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Insert from library" }).ClickAsync();
        await Expect(Page.Locator(".mud-dialog").First).ToBeAttachedAsync();
        await Expect(Page.Locator(".mud-dialog .media-tile").First).ToBeAttachedAsync();

        await probe.StepAsync($"editor existing post, media picker open ({theme})");
        await Page.GotoAsync($"{BaseUrl}/admin/posts/{_bannerId}/edit");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await WaitForInteractiveAsync(Page);
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Edit Post", Level = 1 }))
            .ToBeVisibleAsync();
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Insert from library" }).ClickAsync();
        await Expect(Page.Locator(".mud-dialog").First).ToBeAttachedAsync();
        await Expect(Page.Locator(".mud-dialog .media-tile").First).ToBeAttachedAsync();

        await probe.StepAsync($"draft review, Add feedback up ({theme})");
        await Page.GotoAsync($"{BaseUrl}/editor/posts/{_draftSlug}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.Locator(".post-hero__ribbon")).ToHaveTextAsync("Draft");
        await Page.WaitForFunctionAsync("() => document.documentElement.dataset.selectionWatch === 'on'");
        await Page.Locator(".markdown-body strong").DblClickAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Add feedback" }))
            .ToBeVisibleAsync();

        // The chart only renders once the post has a view, so the public page is visited first and
        // the beacon's response is awaited — the chart page then measures with the chart up.
        await probe.StepAsync($"post page visit for a charted view ({theme})");
        var beacon = Page.WaitForResponseAsync(r =>
            r.Url.EndsWith("/api/analytics/pageview") && r.Request.Method == "POST");
        await Page.GotoAsync($"{BaseUrl}/post/{_bannerSlug}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = BannerTitle, Level = 1 }))
            .ToBeVisibleAsync();
        await beacon;

        await probe.StepAsync($"analytics chart page ({theme})");
        await Page.GotoAsync($"{BaseUrl}/admin/posts/{_bannerId}/analytics");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.Locator(".analytics__total")).ToContainTextAsync("Total views:");
        await Expect(Page.Locator(".views-chart, .views-chart__empty").First).ToBeAttachedAsync();

        await probe.StepAsync($"theme switch toggle on admin posts ({theme})");
        await Page.GotoAsync($"{BaseUrl}/admin/posts");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await WaitForInteractiveAsync(Page);
        await ToggleThemeSwitchAsync();

        await AssertZeroViolationsAsync(probe, $"admin pages, {theme}");
    }

    /// <summary>The mobile contents panel on the post page at 390x844 — the phone viewport where
    /// its three states are meaningful — driven through each state by the clicks the panel itself
    /// handles and drained per state: the in-flow block under the hero, the pinned one-row bar (a
    /// TOC entry tap jumps to the section and pins the row) and the opened drawer (the row button
    /// toggles it). post.js sizes the states only through the CSSOM, which the public policy's
    /// style-src-attr 'none' allows — and this measurement is what keeps it that way, per state,
    /// under the emulated scheme.</summary>
    private async Task MeasureContentsPanelAsync(ColorScheme scheme)
    {
        IgnoreWhereSigningInIsImpossible();
        await EnsureFixturesAsync();
        var probe = await CspViolationProbe.AttachAsync(Page);
        await Page.EmulateMediaAsync(new PageEmulateMediaOptions { ColorScheme = scheme });
        var theme = scheme == ColorScheme.Dark ? "dark" : "light";
        await Page.SetViewportSizeAsync(390, 844);

        await probe.StepAsync($"contents panel at rest ({theme})");
        await Page.GotoAsync($"{BaseUrl}/post/{_panelSlug}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await WaitForInteractiveAsync(Page);
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = PanelTitle, Level = 1 }))
            .ToBeVisibleAsync();
        await Expect(Page.Locator(".toc-inline")).ToBeVisibleAsync();
        await Page.EvaluateAsync("() => document.fonts.ready");
        await Expect(Page.Locator(".toc-inline .toc__link:visible")).ToHaveCountAsync(5);
        await WaitForPanelStateAsync(stateClass: null, expanded: false);

        await probe.StepAsync($"contents panel pinned ({theme})");
        await Page.Locator(".toc-inline")
            .GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = "Section 4", Exact = true })
            .ClickAsync();
        await Expect(Page.Locator(".toc-inline__row-text")).ToHaveTextAsync("Section 4");
        await WaitForPanelStateAsync("toc-inline--pinned", expanded: false);

        await probe.StepAsync($"contents panel opened ({theme})");
        var row = Page.Locator(".toc-inline__row");
        await row.ClickAsync();
        await Expect(row).ToHaveAttributeAsync("aria-expanded", "true");
        await Expect(Page.Locator(".toc-inline .toc__list")).ToBeVisibleAsync();
        await WaitForPanelStateAsync("toc-inline--open", expanded: true);

        await AssertZeroViolationsAsync(probe, $"contents panel at 390x844, {theme}");
    }

    /// <summary>Waits for one contents-panel state to be in place and its ≤200ms transitions to be
    /// done (no running animation) — the state's class and the row's aria-expanded, the concrete
    /// signals MobileContentsPanelTests measures against. A null state class is the in-flow rest
    /// state: enhanced, nothing pinned, nothing open. Never a sleep.</summary>
    private async Task WaitForPanelStateAsync(string? stateClass, bool expanded)
    {
        await Page.WaitForFunctionAsync(
            @"args => {
                const el = document.querySelector('.toc-inline');
                if (el === null || !el.classList.contains('toc-inline--enhanced')) {
                    return false;
                }
                const atRest = !el.classList.contains('toc-inline--pinned')
                    && !el.classList.contains('toc-inline--open');
                const stateOk = args.cls === null ? atRest : el.classList.contains(args.cls);
                return stateOk
                    && el.querySelector('.toc-inline__row')?.getAttribute('aria-expanded') === String(args.expanded)
                    && el.getAnimations({ subtree: true }).length === 0;
            }",
            new { cls = stateClass, expanded });
    }

    /// <summary>One click on the theme switch (the label must actually change — the outcome, not
    /// the click) and the re-render that follows it is what gets measured.</summary>
    private async Task ToggleThemeSwitchAsync()
    {
        var themeSwitch = Page.Locator(".theme-switch");
        var before = (await themeSwitch.TextContentAsync())?.Trim();
        await themeSwitch.ClickAsync();
        await Page.WaitForFunctionAsync(
            "prev => { const b = document.querySelector('.theme-switch'); return b !== null && b.textContent.trim() !== prev; }",
            before);
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

    /// <summary>One fixture set per run (unique via RunId), created through the admin UI: a
    /// category, a published post with the banner image, a published post without one (the
    /// fallback banner), a published static page and a draft with one bold word for the review
    /// selection. The slugs come back from the API; the draft's from its Review link (the public
    /// API hides drafts).</summary>
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

            await LoginAsync("admin@example.com", "password");

            await Page.GotoAsync($"{BaseUrl}/admin/tags");
            await WaitForInteractiveAsync(Page);
            await Page.GetByLabel("New category").FillAsync(CategoryName);
            await Page.Locator(".mud-input-control", new PageLocatorOptions { Has = Page.GetByLabel("New category") })
                .Locator("button").First
                .ClickAsync();
            await Expect(Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = $"Icon for {CategoryName}" }))
                .ToBeVisibleAsync();

            _bannerId = await PublishPostAsync(BannerTitle, $"Banner fixture body {RunId}.", withBanner: true);
            _bannerSlug = await SlugForPostAsync(_bannerId);
            var plainId = await PublishPostAsync(PlainTitle, $"Plain fixture body {RunId}.", withBanner: false);
            _plainSlug = await SlugForPostAsync(plainId);
            _panelSlug = await SlugForPostAsync(
                await PublishPostAsync(PanelTitle, PanelBody(), withBanner: false));
            _staticPageSlug = await SlugForPostAsync(
                await PublishPageAsync(StaticPageTitle, $"Static page fixture body {RunId}."));

            await CreateDraftAsync(DraftTitle, $"The **{QuotedWord}** waits for the rain.");
            _draftSlug = await DraftSlugFromReviewLinkAsync(DraftTitle);

            _categorySlug = await SlugFromApiAsync("api/categories", "name", CategoryName);
            _tagSlug = await SlugFromApiAsync("api/tags", "name", TagName);

            await Page.GotoAsync($"{BaseUrl}/logout");
            _dataReady = true;
        }
        finally
        {
            DataGate.Release();
        }
    }

    /// <summary>The contents-panel fixture's body: twelve ~150-word h2 sections named
    /// "Section N" — a post tall enough for the panel to pin and the drawer to scroll
    /// (MobileContentsPanelTests' body shape), with stable entry names to click.</summary>
    private static string PanelBody()
    {
        const string sentence =
            "when the pinned row collapsed the outline to one line the reader kept their place in " +
            "the long scroll of sections that followed the hero and its standfirst";
        var body = new StringBuilder();
        body.Append($"Fixture intro {RunId} with enough words to start the page comfortably.\n\n");
        for (var n = 1; n <= 12; n++)
        {
            body.Append($"## Section {n}\n\n");
            for (var repeat = 0; repeat < 6; repeat++)
            {
                body.Append(sentence).Append(". ");
            }

            body.Append("\n\n");
        }

        return body.ToString();
    }

    private async Task<Guid> PublishPostAsync(string title, string body, bool withBanner)
    {
        await Page.GotoAsync($"{BaseUrl}/admin/posts/new");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await WaitForInteractiveAsync(Page);
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "New Post", Level = 1 }))
            .ToBeVisibleAsync();

        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync(title);
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).BlurAsync();
        await Page.GetByLabel("Markdown").FillAsync(body);
        await Page.GetByLabel("Markdown").BlurAsync();
        await AddTagsAsync(TagName);

        if (withBanner)
        {
            await Page.GetByLabel("Upload banner")
                .SetInputFilesAsync(Path.Combine(AppContext.BaseDirectory, "test-image.png"));
            await Expect(Page.GetByText("test-image.png").First).ToBeVisibleAsync();
        }

        // Select through the picker's search: the unfiltered list is a bounded window (the first
        // page of name-ordered categories), so a freshly created category can sort outside it.
        var category = Page.GetByRole(AriaRole.Combobox, new PageGetByRoleOptions { Name = "Category" });
        await category.ClickAsync();
        await category.FillAsync(CategoryName);
        await Page.GetByRole(AriaRole.Option, new PageGetByRoleOptions { Name = CategoryName }).ClickAsync();

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Publish now" }).ClickAsync();
        await Page.WaitForURLAsync(
            url => url.StartsWith($"{BaseUrl}/admin/posts/") && url.EndsWith("/edit"),
            new PageWaitForURLOptions { Timeout = 15000 });
        return PostIdFromEditorUrl();
    }

    /// <summary>A published page through the page editor: a page takes no category, tags or
    /// banner, so the form runs title and body only.</summary>
    private async Task<Guid> PublishPageAsync(string title, string body)
    {
        await Page.GotoAsync($"{BaseUrl}/admin/pages/new");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await WaitForInteractiveAsync(Page);

        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync(title);
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).BlurAsync();
        await Page.GetByLabel("Markdown").FillAsync(body);
        await Page.GetByLabel("Markdown").BlurAsync();

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Publish now" }).ClickAsync();
        await Page.WaitForURLAsync(
            url => url.StartsWith($"{BaseUrl}/admin/pages/") && url.EndsWith("/edit"),
            new PageWaitForURLOptions { Timeout = 15000 });
        return PostIdFromEditorUrl();
    }

    private async Task CreateDraftAsync(string title, string body)
    {
        await Page.GotoAsync($"{BaseUrl}/admin/posts/new");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await WaitForInteractiveAsync(Page);
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "New Post", Level = 1 }))
            .ToBeVisibleAsync();

        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync(title);
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).BlurAsync();
        await Page.GetByLabel("Markdown").FillAsync(body);
        await Page.GetByLabel("Markdown").BlurAsync();

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save draft" }).ClickAsync();
        await Page.WaitForURLAsync(
            url => url.StartsWith($"{BaseUrl}/admin/posts/") && url.EndsWith("/edit"),
            new PageWaitForURLOptions { Timeout = 15000 });
    }

    /// <summary>The draft's slug from the Editor list's Review link — the public API cannot see
    /// drafts anonymously, and the review page is addressed by slug.</summary>
    private async Task<string> DraftSlugFromReviewLinkAsync(string title)
    {
        await Page.GotoAsync($"{BaseUrl}/editor");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        var row = Page.Locator(".posts-grid__row", new PageLocatorOptions { HasText = title });
        await Expect(row).ToBeVisibleAsync();
        var href = await row.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = "Review" })
            .GetAttributeAsync("href");
        Assert.That(href, Does.StartWith("/editor/posts/"), "The Review link must lead to the draft review page.");
        return href!["/editor/posts/".Length..];
    }

    private async Task<string> SlugFromApiAsync(string path, string nameProperty, string name)
    {
        await using var response = await Page.APIRequest.GetAsync($"{ApiUrl}/{path}");
        Assert.That(response.Status, Is.EqualTo(200), $"The API's {path} list is not reachable on {ApiUrl}.");
        using var doc = JsonDocument.Parse(await response.TextAsync());
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.GetProperty(nameProperty).GetString() == name)
            {
                return item.GetProperty("slug").GetString()!;
            }
        }

        Assert.Fail($"'{name}' was not created through the admin UI (not in {path}).");
        return string.Empty;
    }
}
