using System.Globalization;
using static Croft.Web.E2E.ColorContrast;

namespace Croft.Web.E2E;

/// <summary>
/// Brief 11a item 5: the footer's profile icons, measured in the browser on the example site
/// (branding.example supplies four profiles). In Chromium and Firefox, light and dark: every
/// icon contrasts at least 3:1 with what is behind it (WCAG 1.4.11 non-text contrast) and its
/// link target is at least 24x24 CSS px (WCAG 2.2 target size); at 320 px wide the footer wraps
/// without horizontal scroll; and Tab reaches every icon with the site's focus ring visible.
/// The engine is chosen in the tests, not by the suite (the SidebarFooterAlignmentTests
/// precedent). Break-it-once (recorded in the commit): drop the aria-label; its bUnit test
/// fails, and these measure the same markup.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class FooterProfileIconsTests : WebTest
{
    [TestCase("chromium", "light")]
    [TestCase("chromium", "dark")]
    [TestCase("firefox", "light")]
    [TestCase("firefox", "dark")]
    public async Task EachIcon_ContrastsAtLeastThreeToOne_AndTargetsAtLeast24By24(string engine, string theme)
    {
        await using var browser = await (engine == "firefox" ? Playwright.Firefox : Playwright.Chromium).LaunchAsync();
        await using var context = await NewContextAsync(browser, 1440, 900, theme);
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        if (theme == "dark")
        {
            await WaitForDarkPaletteAsync(page);
        }

        var canvas = theme == "dark" ? new Rgba(0, 0, 0, 1) : new Rgba(255, 255, 255, 1);
        var icons = page.Locator("footer .site-footer__profile");
        Assert.That(await icons.CountAsync(), Is.EqualTo(4),
            "the example branding supplies four profiles, so the footer shows four icons");
        for (var i = 0; i < 4; i++)
        {
            var link = icons.Nth(i);
            var label = await link.GetAttributeAsync("aria-label");

            // The mark is fill="currentColor", so its computed fill is the footer's link colour;
            // its contrast is that colour against the composited backdrop behind it.
            var json = await link.EvaluateAsync<string[]>(
                @"el => { const chain = []; for (let p = el; p; p = p.parentElement) chain.push(getComputedStyle(p).backgroundColor); return [getComputedStyle(el.querySelector('svg')).fill, ...chain]; }");
            var fill = Parse(json[0]);
            var backdrop = BackdropOf(json[1..], canvas);
            var ratio = Ratio(Composite(fill, backdrop), backdrop);
            Assert.That(ratio, Is.GreaterThanOrEqualTo(3),
                $"{label} ({engine}, {theme}): {json[0]} on the backdrop contrasts {ratio:0.00}:1 (need 3:1 for non-text)");

            var box = await link.BoundingBoxAsync();
            Assert.That(box!.Width, Is.GreaterThanOrEqualTo(24),
                $"{label} ({engine}): the link target is {box.Width:0.#} px wide (WCAG 2.2 wants 24)");
            Assert.That(box.Height, Is.GreaterThanOrEqualTo(24),
                $"{label} ({engine}): the link target is {box.Height:0.#} px tall (WCAG 2.2 wants 24)");
        }
    }

    [TestCase("chromium", "light")]
    [TestCase("chromium", "dark")]
    [TestCase("firefox", "light")]
    [TestCase("firefox", "dark")]
    public async Task At320px_TheFooterWrapsWithoutHorizontalScroll(string engine, string theme)
    {
        await using var browser = await (engine == "firefox" ? Playwright.Firefox : Playwright.Chromium).LaunchAsync();
        await using var context = await NewContextAsync(browser, 320, 844, theme);
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var scrollWidth = await page.EvaluateAsync<double>("() => document.documentElement.scrollWidth");
        Assert.That(scrollWidth, Is.LessThanOrEqualTo(320),
            $"{engine}, {theme}: at 320 px the footer wraps — the document is {scrollWidth} px wide, no horizontal scroll");
        Assert.That(await page.Locator("footer .site-footer__profile").CountAsync(), Is.EqualTo(4),
            "every icon is in the wrapped footer");
    }

    [TestCase("chromium", "light")]
    [TestCase("chromium", "dark")]
    [TestCase("firefox", "light")]
    [TestCase("firefox", "dark")]
    public async Task Tab_ReachesEachIcon_WithTheFocusRingVisible(string engine, string theme)
    {
        await using var browser = await (engine == "firefox" ? Playwright.Firefox : Playwright.Chromium).LaunchAsync();
        await using var context = await NewContextAsync(browser, 1440, 900, theme);
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var hrefs = await page.Locator("footer .site-footer__profile")
            .EvaluateAllAsync<string[]>("els => els.map(el => el.getAttribute('href'))");
        var seen = new HashSet<string>();
        for (var press = 0; press < 250 && seen.Count < hrefs.Length; press++)
        {
            await page.Keyboard.PressAsync("Tab");
            var current = await page.EvaluateAsync<string?>(
                "() => { const el = document.activeElement; return el && el.classList.contains('site-footer__profile') ? el.getAttribute('href') : null; }");
            if (current is null)
            {
                continue;
            }

            seen.Add(current);
            var outline = await page.EvaluateAsync<string[]>(
                "() => { const s = getComputedStyle(document.activeElement); return [s.outlineStyle, s.outlineWidth]; }");
            Assert.That(outline[0], Is.Not.EqualTo("none"),
                $"{engine}, {theme}: a keyboard-focused profile icon draws an outline");
            Assert.That(double.Parse(outline[1].Replace("px", ""), CultureInfo.InvariantCulture),
                Is.GreaterThanOrEqualTo(2), $"{engine}, {theme}: the focus ring is at least 2 px wide");
        }

        Assert.That(seen, Is.EquivalentTo(hrefs),
            $"{engine}, {theme}: Tab reaches each of the footer's {hrefs.Length} profile icons");
    }

    private static async Task<IBrowserContext> NewContextAsync(IBrowser browser, int width, int height, string theme)
    {
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new() { Width = width, Height = height },
            ColorScheme = theme == "dark" ? ColorScheme.Dark : ColorScheme.Light,
        });
        await context.AddInitScriptAsync($"localStorage.setItem('blog-theme', '{theme}')");
        return context;
    }
}
