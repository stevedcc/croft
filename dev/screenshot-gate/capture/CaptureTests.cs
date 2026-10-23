using System.Text.Json;
using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;

namespace ScreenshotGate.Capture;

/// <summary>
/// The screenshot gate's capture step (item 7, ruling R17): the fixed fixture set (seeded by
/// dev/screenshot-gate/seed.sh, read from GATE_OUT/fixtures.json) rendered at 390x844 and
/// 1440x900 in light and dark, saved full-page as {surface}-{width}-{theme}.png in GATE_OUT.
///
/// Every value on these pages is a constant of the seed — no run ids, no clock text (the
/// draft's "Saved hh:mm" label only appears after an in-session save, so the editor is
/// captured on load) — and the admin surfaces capture before any public post page is visited,
/// so the posts grid's Views column reads 0 regardless of beacon timing. Animations are
/// disabled in the screenshot options so a capture can never catch a transition mid-frame.
/// One context per capture (its own viewport and theme preference), login through the mock
/// IdP for the staff surfaces — the ScreenshotTests capture pattern, against a deterministic
/// fixture set instead of the suite's run-keyed one.
/// </summary>
public class CaptureTests : PageTest
{
    private const string AdminEmail = "admin@example.com";
    private const string DraftTitle = "Gate Draft Post";
    private const string OAuthAuthority = "http://localhost:5556/default";

    private static readonly (int Width, int Height)[] Sizes = [(390, 844), (1440, 900)];
    private static readonly string[] Themes = ["light", "dark"];

    /// <summary>Captures every surface at both sizes in both themes into GATE_OUT.</summary>
    [Test]
    public async Task CaptureSurfaces()
    {
        var baseUrl = (Environment.GetEnvironmentVariable("E2E_WEB_URL") ?? "http://localhost:5002").TrimEnd('/');
        var gateOut = Environment.GetEnvironmentVariable("GATE_OUT")
            ?? throw new InvalidOperationException("GATE_OUT is not set.");
        Directory.CreateDirectory(gateOut);

        var fixturesPath = Path.Combine(gateOut, "fixtures.json");
        if (!File.Exists(fixturesPath))
        {
            throw new FileNotFoundException($"Run dev/screenshot-gate/seed.sh first: {fixturesPath} is missing.");
        }

        using var fixtures = JsonDocument.Parse(await File.ReadAllTextAsync(fixturesPath));
        var bannerSlug = fixtures.RootElement.GetProperty("bannerSlug").GetString()!;
        var plainSlug = fixtures.RootElement.GetProperty("plainSlug").GetString()!;
        var draftId = fixtures.RootElement.GetProperty("draftId").GetString()!;

        // The screenshot-stable flags: without them Chromium flips between its GPU and
        // software raster paths per capture and text, borders and rounded corners come out
        // with one of two antialiasings (measured: 15-22 pixels on a few surfaces, bimodal
        // run to run). With the flags the same page renders byte-identically every time.
        await using var browser = await Playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Args =
            [
                // WORKAROUND(W-072, Chromium): Chromium flags for pixel-deterministic screenshots; re-baseline on a Playwright or Chromium upgrade
                "--disable-gpu",
                "--disable-lcd-text",
                "--disable-skia-runtime-opts",
                "--disable-partial-raster",
                // END WORKAROUND(W-072)
                "--font-render-hinting=none",
                "--force-color-profile=srgb",
            ]
        });

        // Admin surfaces first: the posts grid shows the fixture posts' view counts, and this
        // ordering guarantees they read 0 on both sides of the comparison.
        var surfaces = new (string Name, string Path, bool SignIn, Func<IPage, Task> Ready)[]
        {
            ("admin-posts", "/admin/posts", true, async page =>
            {
                await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Posts", Level = 1 }))
                    .ToBeVisibleAsync();
                await Expect(page.Locator(".posts-grid__row").First).ToBeVisibleAsync();
            }),
            ("admin-media", "/admin/media", true, async page =>
            {
                await Expect(page.Locator(".media-tile").First).ToBeVisibleAsync();
            }),
            ("post-editor", $"/admin/posts/{draftId}/edit", true, async page =>
            {
                await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Edit Post", Level = 1 }))
                    .ToBeVisibleAsync();
                await Expect(page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }))
                    .ToHaveValueAsync(DraftTitle);
            }),
            ("home", "/", false, async page =>
            {
                await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Latest Posts" }))
                    .ToBeVisibleAsync();
                await Expect(page.Locator(".post-card").First).ToBeVisibleAsync();
            }),
            ("post-banner", $"/post/{bannerSlug}", false, async page =>
            {
                await Expect(page.Locator(".post-hero__title")).ToHaveTextAsync("Gate Post With Banner");
                await Expect(page.Locator(".markdown-body")).ToBeVisibleAsync();
            }),
            ("post-nobanner", $"/post/{plainSlug}", false, async page =>
            {
                await Expect(page.Locator(".post-hero__title")).ToHaveTextAsync("Gate Post Plain");
                await Expect(page.Locator(".markdown-body")).ToBeVisibleAsync();
            }),
            ("search", "/search", false, async page =>
            {
                // The heading, not the input: the plain input[type=search] is this brief's own
                // change (ruling R15), and the baseline renders MudBlazor's field instead. The
                // page's h1 is what both sides show.
                await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Search", Level = 1 }))
                    .ToBeVisibleAsync();
            }),
        };

        foreach (var surface in surfaces)
        {
            foreach (var (width, height) in Sizes)
            {
                foreach (var theme in Themes)
                {
                    await CaptureAsync(browser, gateOut, baseUrl, surface.Name, surface.Path, surface.SignIn,
                        surface.Ready, width, height, theme);
                    TestContext.Progress.WriteLine($"captured {surface.Name}-{width}-{theme}.png");
                }
            }
        }
    }

    /// <summary>Opens one surface in its own context (viewport + theme) and saves it full-page.</summary>
    private static async Task CaptureAsync(
        IBrowser browser, string gateOut, string baseUrl, string name, string path, bool signIn,
        Func<IPage, Task> ready, int width, int height, string theme)
    {
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ColorScheme = theme == "dark" ? ColorScheme.Dark : ColorScheme.Light,
            ViewportSize = new() { Width = width, Height = height }
        });
        await context.AddInitScriptAsync($"localStorage.setItem('blog-theme', '{theme}')");
        var page = await context.NewPageAsync();

        if (signIn)
        {
            await LoginAsync(page, baseUrl);
        }

        await page.GotoAsync($"{baseUrl}{path}");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await ready(page);
        if (theme == "dark")
        {
            await WaitForDarkPaletteAsync(page);
        }

        var png = await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Type = ScreenshotType.Png,
            FullPage = true,
            Animations = ScreenshotAnimations.Disabled
        });
        await File.WriteAllBytesAsync(Path.Combine(gateOut, $"{name}-{width}-{theme}.png"), png);
    }

    /// <summary>Signs in as the mock admin on the context's own page (WebTest.LoginAsync's flow).</summary>
    private static async Task LoginAsync(IPage page, string baseUrl)
    {
        await page.GotoAsync($"{baseUrl}/login");
        await page.WaitForURLAsync(url => url.StartsWith($"{OAuthAuthority}/authorize", StringComparison.Ordinal),
            new PageWaitForURLOptions { Timeout = 15000 });

        await page.Locator("input[name='username']").FillAsync(AdminEmail);
        await page.Locator("input[type='submit'][value='Sign-in']").ClickAsync();

        await page.Locator("a[href='/logout']").First
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 15000 });
    }

    /// <summary>Waits for the rendered dark palette — the light first-paint flash is not the
    /// state to capture (WebTest.WaitForDarkPaletteAsync's flow: the site's --bg on every
    /// document, MudThemeProvider's palette as well on an interactive one).</summary>
    private static async Task WaitForDarkPaletteAsync(IPage page)
    {
        await page.WaitForFunctionAsync(
            @"() => {
                const isDark = c => { const m = c.match(/rgba?\((\d+),\s*(\d+),\s*(\d+)/); return m !== null && +m[1] === 15 && +m[2] === 23 && +m[3] === 42; };
                // The site's own --bg token paints the page on every document.
                if (!isDark(getComputedStyle(document.body).backgroundColor)) return false;
                // An interactive document also has MudThemeProvider, which flips after first render.
                if (document.documentElement.dataset.render !== 'interactive') return true;
                return isDark(getComputedStyle(document.documentElement).getPropertyValue('--mud-palette-background'));
            }");
    }
}
