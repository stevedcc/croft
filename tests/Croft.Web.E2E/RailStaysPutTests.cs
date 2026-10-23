using System.Text.Json;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;

namespace Croft.Web.E2E;

/// <summary>
/// Brief 07g extra: the rail stays put when the drawer opens. At 960-1279px the collapsed rail
/// and the expanded drawer are the same markup (CSS decides widths/visibility), and the owner
/// measures every icon on screenshots: the menu button jumped right (centred by margin: 0 auto),
/// the logo moved ~29px right (brand block relayout), Home/Search/footer icons ~5px left and
/// ~10px down, category icons ~55px down (the CATEGORIES heading only took space in the drawer).
/// So: for every icon in the sidebar (menu button, logo, Home, Search, page and category icons,
/// More categories, the role-section icons, the footer icons) the bounding box is identical
/// within 1px collapsed vs expanded; the labels are visible only when expanded; clicking the menu
/// button again from the expanded state collapses the drawer; and in both states the logo's
/// horizontal centre equals the icon column's centre (Home's icon) within 1px (the addendum: the
/// owner's collapsed logo sat ~6px left of it).
/// Break-it-once (recorded in the commit): (a) margin: 0 auto back on .rail-toggle, (b) the rail's
/// reserved heading space removed, (c) the collapsed logo misaligned ≈6px left of the icon column —
/// the test fails each time.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class RailStaysPutTests : WebTest
{
    // Three categories with published posts (so the CATEGORIES heading, three category icons and
    // "More categories" render) and one published page marked for navigation (its Description
    // icon joins the Home/Search section) — the icon set the owner measured.
    private const int FixtureCategoryCount = 3;

    private static readonly SemaphoreSlim DataGate = new(1, 1);
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];
    private static bool _dataReady;

    [TestCase(1100, 800)]
    [TestCase(1279, 800)]
    public async Task Rail_SignedOut_IconsStayPutWhenTheDrawerOpens(int width, int height)
    {
        await AssertIconsStayPutAsync(width, height, signIn: false);
    }

    [TestCase(1100, 800)]
    [TestCase(1279, 800)]
    public async Task Rail_SignedInAsAdmin_IconsStayPutWhenTheDrawerOpens(int width, int height)
    {
        await AssertIconsStayPutAsync(width, height, signIn: true);
    }

    private async Task AssertIconsStayPutAsync(int width, int height, bool signIn)
    {
        await EnsureSidebarDataAsync();
        await Page.SetViewportSizeAsync(width, height);
        if (signIn)
        {
            await LoginAsync("admin@example.com", "password");
        }

        await Page.GotoAsync(BaseUrl);
        await WaitForInteractiveAsync(Page);
        var sidebar = Page.Locator("#sidebar-nav");
        await sidebar.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });
        // Fonts are loaded before either measurement: the kept-in-place boxes (headings, brand copy)
        // have text-derived heights that must not shift between the two reads.
        await Page.EvaluateAsync("() => document.fonts.ready");

        // Collapsed rail: every label is out of sight (the Collapse control's label is excluded —
        // its whole control is display:none below 1280px in both states).
        var labels = Page.Locator("#sidebar-nav .label:not(.collapse-toggle .label)");
        var labelCount = await labels.CountAsync();
        Assert.That(labelCount, Is.GreaterThan(0), "the sidebar rendered no labels to compare");
        for (var i = 0; i < labelCount; i++)
        {
            Assert.That(await labels.Nth(i).IsVisibleAsync(), Is.False,
                $"label {i} ('{(await labels.Nth(i).TextContentAsync())?.Trim()}') is visible in the collapsed rail; labels belong to the drawer only.");
        }

        var collapsed = await MeasureIconsAsync();
        Assert.That(collapsed.Select(b => b.Key), Does.Contain("menu").And.Contain("logo").And.Contain("/"),
            "the measured icon set must include the menu button, the logo and Home's icon.");

        // Open the drawer: only the labels appear beside the icons.
        await Page.Locator(".rail-toggle").ClickAsync();
        await Page.WaitForFunctionAsync("() => document.documentElement.classList.contains('nav-open')");
        await Page.WaitForFunctionAsync(
            @"() => { const el = document.querySelector(""#sidebar-nav a.nav-item[href='/'] .label"");
                      return el && el.getBoundingClientRect().width > 0; }");

        for (var i = 0; i < labelCount; i++)
        {
            Assert.That(await labels.Nth(i).IsVisibleAsync(), Is.True,
                $"label {i} ('{(await labels.Nth(i).TextContentAsync())?.Trim()}') is not visible in the expanded drawer.");
        }

        var expanded = await MeasureIconsAsync();
        Assert.That(string.Join(",", expanded.Select(b => b.Key)), Is.EqualTo(string.Join(",", collapsed.Select(b => b.Key))),
            "the expanded drawer shows a different set of icons than the collapsed rail.");
        for (var i = 0; i < collapsed.Count; i++)
        {
            var (before, after) = (collapsed[i], expanded[i]);
            Assert.That(after.X, Is.EqualTo(before.X).Within(1), IconMove(before, after, width, height, "x"));
            Assert.That(after.Y, Is.EqualTo(before.Y).Within(1), IconMove(before, after, width, height, "y"));
            Assert.That(after.Width, Is.EqualTo(before.Width).Within(1), IconMove(before, after, width, height, "width"));
            Assert.That(after.Height, Is.EqualTo(before.Height).Within(1), IconMove(before, after, width, height, "height"));
        }

        AssertLogoOnIconColumn(collapsed, "collapsed rail");
        AssertLogoOnIconColumn(expanded, "expanded drawer");

        // Clicking the menu button again collapses the drawer.
        await Page.Locator(".rail-toggle").ClickAsync();
        await Page.WaitForFunctionAsync("() => !document.documentElement.classList.contains('nav-open')");
        await Expect(Page.Locator(".rail-toggle")).ToHaveAttributeAsync("aria-expanded", "false");
        await Page.WaitForFunctionAsync(
            "() => Math.abs(document.querySelector('#sidebar-nav').getBoundingClientRect().width - 64) < 1");
    }

    private static string IconMove(IconBox before, IconBox after, int width, int height, string edge) =>
        $"at {width}x{height} the {before.Key} icon's {edge} moved when the drawer opened: " +
        $"rail ({before.X:0.##}, {before.Y:0.##}, {before.Width:0.##}x{before.Height:0.##}) → " +
        $"drawer ({after.X:0.##}, {after.Y:0.##}, {after.Width:0.##}x{after.Height:0.##}).";

    /// <summary>The logo's horizontal centre sits on the icon column (Home's icon's centre) — the
    /// addendum's alignment — in both sidebar states.</summary>
    private static void AssertLogoOnIconColumn(IReadOnlyList<IconBox> boxes, string state)
    {
        var logo = boxes.Single(b => b.Key == "logo");
        var home = boxes.Single(b => b.Key == "/");
        var logoCentre = logo.X + logo.Width / 2;
        var iconCentre = home.X + home.Width / 2;
        Assert.That(logoCentre, Is.EqualTo(iconCentre).Within(1),
            $"in the {state} the logo's horizontal centre is {logoCentre:0.##} but the icon column's (Home's icon) is {iconCentre:0.##}.");
    }

    /// <summary>One sidebar icon's viewport box, keyed by what it belongs to (menu button, logo,
    /// the owner link's href or title). Icons without a box (the Collapse control's, display:none
    /// below 1280px) are not measured in either state.</summary>
    private sealed record IconBox(string Key, double X, double Y, double Width, double Height);

    private async Task<IReadOnlyList<IconBox>> MeasureIconsAsync()
    {
        var json = await Page.EvaluateAsync<string>(
            @"() => {
                const sidebar = document.querySelector('#sidebar-nav');
                return JSON.stringify([...sidebar.querySelectorAll('svg.category-icon, img.brand-logo')]
                    .map((el, i) => {
                        const owner = el.closest('a, button');
                        const key = el.classList.contains('brand-logo') ? 'logo'
                            : owner?.classList.contains('rail-toggle') ? 'menu'
                            : (owner?.getAttribute('href') ?? owner?.getAttribute('title') ?? ('icon-' + i));
                        const r = el.getBoundingClientRect();
                        return { key, x: r.x, y: r.y, w: r.width, h: r.height };
                    })
                    .filter(b => b.w > 0 && b.h > 0));
            }");
        using var doc = JsonDocument.Parse(json!);
        return
        [
            .. doc.RootElement.EnumerateArray().Select(b => new IconBox(
                b.GetProperty("key").GetString()!,
                b.GetProperty("x").GetDouble(),
                b.GetProperty("y").GetDouble(),
                b.GetProperty("w").GetDouble(),
                b.GetProperty("h").GetDouble()))
        ];
    }

    /// <summary>Three categories with published posts and one published page marked for navigation,
    /// created through the API as the mock admin (MockIdpApi) once per run. Category creation is
    /// idempotent by slug (CategoryService.CreateAsync returns the existing category).</summary>
    private async Task EnsureSidebarDataAsync()
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

            for (var i = 1; i <= FixtureCategoryCount; i++)
            {
                var name = $"Rail Fixture {i}";
                var category = await MockIdpApi.PostJsonAsync(client, ApiUrl, "/api/categories", JsonSerializer.Serialize(name), token);
                var categoryId = category.RootElement.GetProperty("id").GetString()!;
                var post = await MockIdpApi.PostJsonAsync(client, ApiUrl, "/api/posts", JsonSerializer.Serialize(new
                {
                    title = $"{name} Post {RunId}",
                    markdownContent = "Fixture post so this category shows in the sidebar rail.",
                    categoryId
                }), token);
                var postId = post.RootElement.GetProperty("id").GetString()!;
                await MockIdpApi.PostJsonAsync(client, ApiUrl, $"/api/posts/{postId}/publish", "{}", token);
            }

            var page = await MockIdpApi.PostJsonAsync(client, ApiUrl, "/api/posts", new CreatePostRequest(
                $"Rail Nav Page {RunId}",
                "Fixture page so the Home/Search section carries its own icon.",
                Kind: PostKind.Page,
                NavigationPlacement: NavigationPlacement.Sidebar), token);
            var pageId = page.RootElement.GetProperty("id").GetString()!;
            await MockIdpApi.PostJsonAsync(client, ApiUrl, $"/api/posts/{pageId}/publish", "{}", token);

            _dataReady = true;
        }
        finally
        {
            DataGate.Release();
        }
    }
}
