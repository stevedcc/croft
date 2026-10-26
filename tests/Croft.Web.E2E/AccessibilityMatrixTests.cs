using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;

namespace Croft.Web.E2E;

/// <summary>
/// Brief 13g item 1: the axe scan matrix. Every public route and every admin route, at 1440 x 900
/// and 390 x 844, in the light and the dark theme; at phone width the drawer and (on a post) the
/// inline contents panel are opened and scanned too. Every violation fails whatever its impact
/// (minor too: the search field's wrong ARIA role is only a minor finding, aria-allowed-role). The 2.5.8 rule (target-size) is enabled explicitly, and a scan
/// in which it never ran fails (a rule that did not run must not count as a pass).
///
/// Known failures live in one list, <see cref="Known"/> (rule + route + reason + the item that
/// removes it). The brief ends with it empty.
///
/// The matrix is split over three fixture classes so the runner can spread it over its instances
/// (a fixture runs its tests one after another, and the matrix is about seventy scans); each class
/// builds its own fixtures through the API in a one-time setup, so nothing is shared between
/// instances.
/// </summary>
public abstract class AccessibilityScan : StaticReadingTest
{
    /// <summary>One allowed failure: rule id, route key, why, and which brief item removes it.</summary>
    protected sealed record KnownViolation(string Rule, string Route, string Reason, string RemovedBy);

    /// <summary>The explicit allow-list of known violations (rule + route + reason + the item that removes
    /// it). Brief 13g ended with it empty: every case item 1 recorded as red has been fixed.</summary>
    protected static readonly IReadOnlyList<KnownViolation> Known = [];

    /// <summary>Per fixture instance: two matrix classes on one stack instance must not share (and duplicate) their posts.</summary>
    protected string RunId { get; } = Guid.NewGuid().ToString("N")[..10];

    protected static readonly string[] Themes = ["light", "dark"];
    protected static readonly (int Width, int Height)[] Viewports = [(1440, 900), (390, 844)];

    protected Guid PostId { get; private set; }
    protected string PostSlug { get; private set; } = string.Empty;
    protected string CategorySlug { get; private set; } = string.Empty;
    protected string TagSlug { get; private set; } = string.Empty;
    protected string PageSlug { get; private set; } = string.Empty;
    protected string DraftTitle { get; private set; } = string.Empty;
    protected Guid DraftId { get; private set; }
    protected string DraftSlug { get; private set; } = string.Empty;
    private bool _built;

    /// <summary>Builds the fixtures through the API as the mock admin: a published post (category,
    /// two tags, banner, subtitle, three sections and a code block), a published static page and a
    /// draft. Run-keyed, so the shared instance database never matters. A one-time setup of the
    /// fixture, after WebTest.StackUrls has pinned the instance.</summary>
    protected async Task EnsureFixturesAsync()
    {
        if (_built)
        {
            return;
        }

        using var client = MockIdpApi.CreateClient();
        var token = await MockIdpApi.GetAdminAccessTokenAsync(client);
        var category = await CreateCategoryAsync($"Scan {RunId}");
        CategorySlug = category.Slug;
        var banner = await MockIdpApi.UploadImageAsync(client, ApiUrl, $"scan-{RunId}.png",
            await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "test-image.png")), token);
        TagSlug = $"scantag{RunId}";

        var created = await MockIdpApi.PostJsonAsync(client, ApiUrl, "/api/posts",
            new CreatePostRequest($"Scan post {RunId}", BodyWithSections(RunId, 12), Subtitle: "A subtitle for the hero",
                BannerImageId: banner, Tags: [TagSlug, $"scansecond{RunId}"], CategoryId: category.Id), token);
        PostId = Guid.Parse(created.RootElement.GetProperty("id").GetString()!);
        var published = await MockIdpApi.PostJsonAsync(client, ApiUrl, $"/api/posts/{PostId}/publish", "{}", token);
        PostSlug = published.RootElement.GetProperty("slug").GetString()!;

        // The scanned post carries the announcements' counts (brief 17e), so the pill on the hero and on the home card is inside the scans
        // of both themes and both viewports (white on its dark backdrop must hold over whatever banner is under it).
        await using (var db = await OpenInstanceDatabaseAsync())
        {
            await DiscussionSeed.LinkAsync(db, PostId, PostSlug, RunId, counts: (new Croft.Shared.Contracts.Social.SocialCountsDto(5, 2, 1), new Croft.Shared.Contracts.Social.SocialCountsDto(7, 3, 4)));
        }

        DiscussionSeed.WaitForCounts(ApiUrl, PostSlug);

        PageSlug = (await PublishAsync($"Scan page {RunId}", $"A static page {RunId}.", kind: PostKind.Page)).Slug;

        DraftTitle = $"Scan draft {RunId}";
        var draft = await MockIdpApi.PostJsonAsync(client, ApiUrl, "/api/posts",
            new CreatePostRequest(DraftTitle, $"Draft body {RunId} with a [link](/) in it."), token);
        DraftId = Guid.Parse(draft.RootElement.GetProperty("id").GetString()!);
        DraftSlug = draft.RootElement.GetProperty("slug").GetString()!;
        _built = true;
    }

    protected async Task<IBrowserContext> NewScanContextAsync(int width, int height, string theme)
    {
        var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ColorScheme = ColorScheme.Light,
            ViewportSize = new() { Width = width, Height = height },
            HasTouch = width < 600,
        });
        if (theme == "dark")
        {
            await context.AddInitScriptAsync("localStorage.setItem('blog-theme', 'dark')");
        }

        return context;
    }

    /// <summary>A scan result for one page state: what failed, and whether the 2.5.8 rule ran.</summary>
    protected sealed record ScanOutcomeView(string State, IReadOnlyList<AxeResultItem> Failing, bool TargetSizeRan);

    protected static async Task<ScanOutcomeView> RunScanAsync(IPage page, string state)
    {
        var options = new AxeRunOptions
        {
            Rules = new Dictionary<string, RuleOptions>
            {
                ["target-size"] = new RuleOptions { Enabled = true },
                ["color-contrast"] = new RuleOptions { Enabled = true },
            },
        };
        AxeResult axe = await page.RunAxe(options);
        // A rule ran when axe put it in any of the four result lists; a disabled rule is in none.
        var ran = axe.Passes.Concat(axe.Violations).Concat(axe.Incomplete).Concat(axe.Inapplicable).Any(r => r.Id == "target-size");
        return new ScanOutcomeView(state, [.. axe.Violations], ran);
    }

    private static string Describe(IEnumerable<(string State, AxeResultItem Violation)> failures) =>
        string.Join("\n\n", failures.Select(f =>
            $"[{f.State}] {f.Violation.Id} ({f.Violation.Impact}): {f.Violation.Help}\n{f.Violation.HelpUrl}\n" +
            string.Join("\n", f.Violation.Nodes.Take(5).Select(n => $"  {n.Target} :: {n.Html}"))));

    /// <summary>Scans the page in each of the states, asserts target-size ran in every scan and that
    /// nothing outside the allow-list failed. Failing lines name the state (page, drawer, contents).</summary>
    protected static void AssertScans(string route, string where, IEnumerable<ScanOutcomeView> scans)
    {
        var list = scans.ToList();
        var neverRan = list.Where(s => !s.TargetSizeRan).Select(s => s.State).ToList();
        Assert.That(neverRan, Is.Empty, $"the target-size rule did not run in the scan of {where}: a rule that never ran is not a pass");
        var failures = list.SelectMany(s => s.Failing.Select(v => (s.State, Violation: v)))
            .Where(f => !Known.Any(k => k.Rule == f.Violation.Id && k.Route == route))
            .ToList();
        if (failures.Count > 0)
        {
            Assert.Fail($"axe violations on {where}:\n{Describe(failures)}");
        }
    }

    /// <summary>At phone width: opens the drawer, scans it, closes it again.</summary>
    protected static async Task<ScanOutcomeView> ScanDrawerAsync(IPage page)
    {
        var button = page.Locator(".menu-button");
        await button.ClickAsync();
        await Assertions.Expect(button).ToHaveAttributeAsync("aria-expanded", "true");
        var outcome = await RunScanAsync(page, "drawer open");
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(button).ToHaveAttributeAsync("aria-expanded", "false");
        return outcome;
    }

    /// <summary>Filters the posts grid to drafts and waits until the filter has really been applied: the
    /// chip shows it pressed (the circuit handled the click) and the grid is no longer loading, so the
    /// row the next click aims at is the one that stays. (The shared instance database holds other
    /// posts, which is why the filter is used at all.)</summary>
    protected static async Task FilterToDraftsAsync(IPage page, string draftTitle)
    {
        var draft = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Draft", Exact = true });
        await draft.ClickAsync();
        await Assertions.Expect(draft).ToHaveAttributeAsync("aria-pressed", "true");
        await Assertions.Expect(page.Locator(".mud-table-loading")).ToHaveCountAsync(0);
        await Assertions.Expect(page.Locator(".posts-grid__row", new PageLocatorOptions { HasText = draftTitle })).ToBeVisibleAsync(new() { Timeout = 30_000 });
    }

    /// <summary>Waits until a page is usable: the shell JS ran and, in the dark theme, the dark palette shows.</summary>
    protected static async Task WaitUsableAsync(IPage page, string theme)
    {
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await WaitForInteractiveAsync(page);
        if (theme == "dark")
        {
            await WaitForDarkPaletteAsync(page);
        }
    }
}

/// <summary>The public routes of the matrix (brief 13g item 1).</summary>
[Parallelizable(ParallelScope.Fixtures)]
public class AccessibilityPublicMatrixTests : AccessibilityScan
{
    private static readonly string[] Routes = ["home", "post", "categories", "category", "tag", "search-empty", "search-query", "page", "not-found", "not-found-catch-all"];

    public static IEnumerable<TestCaseData> Cases() =>
        from route in Routes
        from viewport in Viewports
        from theme in Themes
        select new TestCaseData(route, viewport.Width, viewport.Height, theme)
            .SetName($"Scan_{route}_{viewport.Width}_{theme}");

    private static async Task WaitForPanelAsync(IPage page, string stateClass) =>
        await page.WaitForFunctionAsync(
            @"cls => { const el = document.querySelector('.toc-inline'); return el !== null && el.classList.contains(cls) && el.getAnimations({ subtree: true }).length === 0; }",
            stateClass);

    private (string Path, string Ready) Resolve(string route) => route switch
    {
        "home" => ("/", ".post-card"),
        "post" => ($"/post/{PostSlug}", "pre code.hljs"),
        "categories" => ("/categories", "main h1"),
        "category" => ($"/category/{CategorySlug}", ".post-card"),
        "tag" => ($"/tag/{TagSlug}", ".post-card"),
        "search-empty" => ("/search", "input[type=search]"),
        "search-query" => ($"/search?q={RunId}", ".post-card"),
        "page" => ($"/{PageSlug}", "main h1"),
        // The two renderings of the not-found copy: the static reading page's and the interactive catch-all's.
        "not-found" => ($"/post/missing-{RunId}", "main :is(h1, h3)"),
        "not-found-catch-all" => ($"/no/such/path-{RunId}", "main :is(h1, h3)"),
        _ => throw new ArgumentOutOfRangeException(nameof(route), route, null),
    };

    [TestCaseSource(nameof(Cases))]
    public async Task Scan(string route, int width, int height, string theme)
    {
        await EnsureFixturesAsync();
        await using var context = await NewScanContextAsync(width, height, theme);
        var page = await context.NewPageAsync();
        var (path, ready) = Resolve(route);
        await page.GotoAsync($"{BaseUrl}{path}");
        await Expect(page.Locator(ready).First).ToBeVisibleAsync();
        await WaitUsableAsync(page, theme);

        var scans = new List<ScanOutcomeView> { await RunScanAsync(page, "page") };
        if (width < 600)
        {
            scans.Add(await ScanDrawerAsync(page));
            if (route == "post")
            {
                // The panel is an in-flow block until the reader scrolls; then it pins as a one-row bar
                // that opens on a tap (MobileContentsPanelTests drives it the same way).
                await page.EvaluateAsync("() => document.getElementById('section-6').scrollIntoView()");
                await WaitForPanelAsync(page, "toc-inline--pinned");
                scans.Add(await RunScanAsync(page, "contents panel pinned"));
                var panelRow = page.Locator(".toc-inline__row");
                await panelRow.ClickAsync();
                await Expect(panelRow).ToHaveAttributeAsync("aria-expanded", "true");
                await WaitForPanelAsync(page, "toc-inline--open");
                scans.Add(await RunScanAsync(page, "contents panel open"));
            }
        }

        AssertScans(route, $"{route} at {width}px, {theme}", scans);
    }
}

/// <summary>The admin and editor routes of the matrix (brief 13g item 1).</summary>
[Parallelizable(ParallelScope.Fixtures)]
public class AccessibilityAdminMatrixTests : AccessibilityScan
{
    private static readonly string[] Routes = ["posts", "post-editor", "editor-list", "editor-review", "media", "tags", "pages", "post-analytics", "hidden-replies"];

    public static IEnumerable<TestCaseData> Cases() =>
        from route in Routes
        from viewport in Viewports
        from theme in Themes
        select new TestCaseData(route, viewport.Width, viewport.Height, theme)
            .SetName($"Scan_{route}_{viewport.Width}_{theme}");

    [TestCaseSource(nameof(Cases))]
    public async Task Scan(string route, int width, int height, string theme)
    {
        await EnsureFixturesAsync();
        await using var context = await NewScanContextAsync(width, height, theme);
        var page = await context.NewPageAsync();
        await LoginAsync(page, route.StartsWith("editor", StringComparison.Ordinal) ? "editor@example.com" : "admin@example.com", "password");

        var (path, ready) = route switch
        {
            "posts" => ("/admin/posts", ".posts-grid__row"),
            "post-editor" => ($"/admin/posts/{DraftId}/edit", "input[aria-label='Title'], .post-actions"),
            "editor-list" => ("/editor", ".posts-grid__row"),
            "editor-review" => ($"/editor/posts/{DraftSlug}", ".post-hero__ribbon"),
            "media" => ("/admin/media", "main h1"),
            "tags" => ("/admin/tags", ".admin-table"),
            "pages" => ("/admin/pages", "main h1"),
            "post-analytics" => ($"/admin/posts/{PostId}/analytics", "main h1"),
            "hidden-replies" => ("/admin/discussion/blocks", "main h1"),
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, null),
        };
        await page.GotoAsync($"{BaseUrl}{path}");
        await Expect(page.Locator(ready).First).ToBeVisibleAsync(new() { Timeout = SlowLoadTimeoutMs });
        await WaitUsableAsync(page, theme);
        if (route == "posts")
        {
            await FilterToDraftsAsync(page, DraftTitle);
        }

        var scans = new List<ScanOutcomeView> { await RunScanAsync(page, "page") };
        if (width < 600)
        {
            scans.Add(await ScanDrawerAsync(page));
        }

        AssertScans(route, $"{route} at {width}px, {theme}", scans);
    }
}

/// <summary>The MudDialogs the admin opens, each opened once and scanned (brief 13g item 1).</summary>
[Parallelizable(ParallelScope.Fixtures)]
public class AccessibilityDialogMatrixTests : AccessibilityScan
{
    public static IEnumerable<TestCaseData> Cases() =>
        from dialog in new[] { "delete-confirm", "media-picker", "icon-picker" }
        from theme in Themes
        select new TestCaseData(dialog, theme).SetName($"ScanDialog_{dialog}_{theme}");

    [TestCaseSource(nameof(Cases))]
    public async Task ScanDialog(string dialog, string theme)
    {
        await EnsureFixturesAsync();
        await using var context = await NewScanContextAsync(1440, 900, theme);
        var page = await context.NewPageAsync();
        await LoginAsync(page, "admin@example.com", "password");

        switch (dialog)
        {
            case "delete-confirm":
                await page.GotoAsync($"{BaseUrl}/admin/posts");
                await WaitUsableAsync(page, theme);
                await FilterToDraftsAsync(page, DraftTitle);
                var row = page.Locator(".posts-grid__row", new PageLocatorOptions { HasText = DraftTitle });
                await row.Locator(".posts-grid__delete").ClickAsync();
                break;
            case "media-picker":
                await page.GotoAsync($"{BaseUrl}/admin/posts/{DraftId}/edit");
                await WaitUsableAsync(page, theme);
                await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Choose from library" }).ClickAsync();
                break;
            case "icon-picker":
                await page.GotoAsync($"{BaseUrl}/admin/tags");
                await WaitUsableAsync(page, theme);
                await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = $"Icon for Scan {RunId}" }).ClickAsync();
                break;
        }

        await Expect(page.Locator(".mud-dialog")).ToBeVisibleAsync(new() { Timeout = SlowLoadTimeoutMs });
        // Let the dialog's open transition end before the scan reads colours.
        await page.WaitForFunctionAsync("() => document.getAnimations().every(a => a.playState !== 'running')");

        AssertScans($"dialog:{dialog}", $"the {dialog} dialog, {theme}", [await RunScanAsync(page, dialog)]);
    }

    /// <summary>W-095's error colour reaches a dialog (brief 13i item 6): the dialogs render outside the
    /// layout's shell, where the colour used to be defined. No dialog has a text-variant error button
    /// today, so the test puts one into the open delete-confirm dialog, built as MudBlazor builds it,
    /// and measures its label against the dialog's own surface.</summary>
    [TestCase("light")]
    [TestCase("dark")]
    public async Task ATextVariantErrorButtonInADialog_HasAtLeast45To1Contrast(string theme)
    {
        await EnsureFixturesAsync();
        await using var context = await NewScanContextAsync(1440, 900, theme);
        var page = await context.NewPageAsync();
        await LoginAsync(page, "admin@example.com", "password");
        await page.GotoAsync($"{BaseUrl}/admin/posts");
        await WaitUsableAsync(page, theme);
        await FilterToDraftsAsync(page, DraftTitle);
        var row = page.Locator(".posts-grid__row", new PageLocatorOptions { HasText = DraftTitle });
        await row.Locator(".posts-grid__delete").ClickAsync();
        await Expect(page.Locator(".mud-dialog")).ToBeVisibleAsync(new() { Timeout = SlowLoadTimeoutMs });
        await page.WaitForFunctionAsync("() => document.getAnimations().every(a => a.playState !== 'running')");

        var colours = await page.EvaluateAsync<string[]>("""
            () => {
                const dialog = document.querySelector('.mud-dialog');
                const button = document.createElement('button');
                button.type = 'button';
                button.className = 'mud-button-root mud-button mud-button-text mud-button-text-error mud-button-text-size-small';
                button.textContent = 'Delete';
                dialog.appendChild(button);
                return [getComputedStyle(button).color, getComputedStyle(dialog).backgroundColor];
            }
            """);
        var (label, surface) = (colours[0], colours[1]);

        var ratio = ColorContrast.Ratio(
            ColorContrast.Composite(ColorContrast.Parse(label), ColorContrast.Parse(surface)), ColorContrast.Parse(surface));
        Assert.That(ratio, Is.GreaterThanOrEqualTo(4.5),
            $"the error label {label} on the dialog surface {surface} in the {theme} theme");
    }
}
