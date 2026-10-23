using static Croft.Web.E2E.ColorContrast;
using Croft.Shared.Enums;

namespace Croft.Web.E2E;

/// <summary>
/// Brief 10f item 2: every document ends with the footer — the owner's footer pages in their
/// order, then "Running on Croft v{version}". Measured in the browser on Home, a post, a static page
/// and an admin page, at 1440 and 390 px: the footer lists the run's footer pages in the order
/// number (not alphabetical), the Sidebar page is not in the footer and the Footer pages are not
/// in the sidebar; the document has exactly one contentinfo landmark; a footer link leads to its
/// page; the footer text and links read at 4.5:1 or better on what is behind them, light and dark;
/// a footer link shows the focus ring from the keyboard.
/// Break-it-once (recorded in the commit): drop the footer from MainLayout; these tests fail.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class SiteFooterTests : StaticReadingTest
{
    /// <summary>The released version, read from the one &lt;Version&gt; in Directory.Build.props
    /// (the app stamps the same value at build): never a literal here, so a version bump cannot
    /// leave this test behind.</summary>
    private static readonly string Version = ReadVersionFromBuildProps();
    private static string ReadVersionFromBuildProps()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Croft.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.That(dir, Is.Not.Null, "the tests run from inside the repository");
        var props = File.ReadAllText(Path.Combine(dir!.FullName, "Directory.Build.props"));
        var match = System.Text.RegularExpressions.Regex.Match(props, @"<Version>\s*([^<\s]+)\s*</Version>");
        Assert.That(match.Success, Is.True, "Directory.Build.props has a <Version>");
        return match.Groups[1].Value;
    }

    private static readonly SemaphoreSlim DataGate = new(1, 1);
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];
    private static bool _dataReady;
    private static string _postSlug = string.Empty;
    private static string _sidebarSlug = string.Empty;
    private static string _footerFirstSlug = string.Empty;
    private static string _footerSecondSlug = string.Empty;

    // Order 1 has the title that sorts last: a footer sorted by title instead of by order fails.
    private static string FooterFirstTitle => $"Footer Zulu {RunId}";
    private static string FooterSecondTitle => $"Footer Alpha {RunId}";
    private static string SidebarTitle => $"Footer Side {RunId}";

    [TestCase(1440, 900, "/")]
    [TestCase(390, 844, "/")]
    [TestCase(1440, 900, "post")]
    [TestCase(390, 844, "post")]
    [TestCase(1440, 900, "page")]
    [TestCase(390, 844, "page")]
    [TestCase(1440, 900, "/admin/tags")]
    [TestCase(390, 844, "/admin/tags")]
    public async Task EveryDocument_EndsWithTheFooterPagesInOrder_ThenTheVersionLine(int width, int height, string where)
    {
        await EnsureFixtureAsync();
        await using var context = await NewContextAsync(width, height, "light");
        var page = await context.NewPageAsync();
        await OpenAsync(page, where);

        var footer = page.Locator("footer[role=contentinfo]");
        await Expect(footer).ToHaveCountAsync(1);
        await Expect(footer).ToBeVisibleAsync();
        await Expect(page.Locator("footer")).ToHaveCountAsync(1, new() { Timeout = 5000 });

        var titles = await footer.Locator("a").AllTextContentsAsync();
        var mine = titles.Where(t => t.EndsWith(RunId, StringComparison.Ordinal)).ToList();
        Assert.That(mine, Is.EqualTo(new[] { FooterFirstTitle, FooterSecondTitle }),
            $"the footer lists this run's footer pages by their order number on {where} at {width}px");
        Assert.That(titles, Does.Not.Contain(SidebarTitle), "a Sidebar page is not in the footer");

        var text = System.Text.RegularExpressions.Regex.Replace(await footer.InnerTextAsync(), @"\s+", " ").Trim();
        Assert.That(text, Does.EndWith($"Running on Croft v{Version}"),
            "the footer ends with the released version, stamped at build");

        var sidebarLinks = await page.Locator("#sidebar-nav a").EvaluateAllAsync<string[]>("els => els.map(a => a.getAttribute('href'))");
        Assert.That(sidebarLinks, Does.Contain($"/{_sidebarSlug}"), "the Sidebar page is in the sidebar");
        Assert.That(sidebarLinks, Does.Not.Contain($"/{_footerFirstSlug}").And.Not.Contain($"/{_footerSecondSlug}"),
            "a Footer page is not in the sidebar");

        // The footer follows the page content: it sits below main's content, not fixed over it.
        var position = await footer.EvaluateAsync<string>("el => getComputedStyle(el).position");
        Assert.That(position, Is.Not.EqualTo("fixed").And.Not.EqualTo("sticky"));
    }

    [Test]
    public async Task AFooterLink_LeadsToItsPage()
    {
        await EnsureFixtureAsync();
        await using var context = await NewContextAsync(1440, 900, "light");
        var page = await context.NewPageAsync();
        await page.GotoAsync(BaseUrl);

        await page.Locator("footer").GetByRole(AriaRole.Link, new() { Name = FooterFirstTitle }).ClickAsync();

        await page.WaitForURLAsync($"{BaseUrl}/{_footerFirstSlug}");
        await Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToHaveTextAsync(FooterFirstTitle);
        await Expect(page.Locator("footer[role=contentinfo]")).ToHaveCountAsync(1);
    }

    [TestCase(1440, 900, "light")]
    [TestCase(1440, 900, "dark")]
    [TestCase(390, 844, "light")]
    [TestCase(390, 844, "dark")]
    public async Task FooterTextAndLinks_ReadAtFourAndAHalfToOne(int width, int height, string theme)
    {
        await EnsureFixtureAsync();
        await using var context = await NewContextAsync(width, height, theme);
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/post/{_postSlug}");
        if (theme == "dark")
        {
            await WaitForDarkPaletteAsync(page);
        }

        var canvas = theme == "dark" ? new Rgba(0, 0, 0, 1) : new Rgba(255, 255, 255, 1);
        var samples = new[] { "footer .site-footer__powered", "footer a" };
        foreach (var selector in samples)
        {
            var element = page.Locator(selector).First;
            await Expect(element).ToBeVisibleAsync();
            var json = await element.EvaluateAsync<string[]>(
                @"el => { const chain = []; for (let p = el; p; p = p.parentElement) chain.push(getComputedStyle(p).backgroundColor); return [getComputedStyle(el).color, ...chain]; }");
            var text = Parse(json[0]);
            var backdrop = BackdropOf(json[1..], canvas);
            var ratio = Ratio(Composite(text, backdrop), backdrop);
            Assert.That(ratio, Is.GreaterThanOrEqualTo(4.5),
                $"{selector} ({theme}, {width}px): {json[0]} on the backdrop contrasts {ratio:0.00}:1");
        }
    }

    [Test]
    public async Task AFooterLink_ShowsTheFocusRingFromTheKeyboard()
    {
        await EnsureFixtureAsync();
        await using var context = await NewContextAsync(1440, 900, "light");
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/post/{_postSlug}");

        var link = page.Locator("footer").GetByRole(AriaRole.Link, new() { Name = FooterFirstTitle });
        await link.FocusAsync();
        await page.Keyboard.PressAsync("Shift+Tab");
        await page.Keyboard.PressAsync("Tab");

        await Expect(link).ToBeFocusedAsync();
        var outline = await link.EvaluateAsync<string[]>(
            "el => { const s = getComputedStyle(el); return [s.outlineStyle, s.outlineWidth]; }");
        Assert.That(outline[0], Is.Not.EqualTo("none"), "a keyboard-focused footer link draws an outline");
        Assert.That(double.Parse(outline[1].Replace("px", ""), System.Globalization.CultureInfo.InvariantCulture),
            Is.GreaterThanOrEqualTo(2));
    }

    private async Task OpenAsync(IPage page, string where)
    {
        if (where.StartsWith("/admin", StringComparison.Ordinal))
        {
            await LoginAsync(page, "admin@example.com", "password");
            await page.GotoAsync($"{BaseUrl}{where}");
            await WaitForInteractiveAsync(page);
            return;
        }

        var path = where switch
        {
            "post" => $"/post/{_postSlug}",
            "page" => $"/{_sidebarSlug}",
            _ => where,
        };
        await page.GotoAsync($"{BaseUrl}{path}");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }

    private async Task<IBrowserContext> NewContextAsync(int width, int height, string theme)
    {
        var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new() { Width = width, Height = height },
            ColorScheme = theme == "dark" ? ColorScheme.Dark : ColorScheme.Light,
        });
        await context.AddInitScriptAsync($"localStorage.setItem('blog-theme', '{theme}')");
        return context;
    }

    private async Task EnsureFixtureAsync()
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

            var post = await PublishAsync($"Footer Post {RunId}", "A post so the footer shows under a post page.");
            _postSlug = post.Slug;
            _sidebarSlug = (await PublishAsync(SidebarTitle, "A static page placed in the sidebar.",
                kind: PostKind.Page, placement: NavigationPlacement.Sidebar)).Slug;
            _footerSecondSlug = (await PublishAsync(FooterSecondTitle, "Second footer page.",
                kind: PostKind.Page, placement: NavigationPlacement.Footer, navigationOrder: 2)).Slug;
            _footerFirstSlug = (await PublishAsync(FooterFirstTitle, "First footer page.",
                kind: PostKind.Page, placement: NavigationPlacement.Footer, navigationOrder: 1)).Slug;
            _dataReady = true;
        }
        finally
        {
            DataGate.Release();
        }
    }
}
