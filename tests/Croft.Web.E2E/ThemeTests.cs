using System.Text.Json;

namespace Croft.Web.E2E;

[Parallelizable(ParallelScope.Fixtures)]
public class ThemeTests : WebTest
{
    private static readonly SemaphoreSlim DataGate = new(1, 1);
    private static string? _themePostSlug;

    [Test]
    public async Task NoJs_SystemDark_RendersDarkBackground()
    {
        // WORKAROUND(W-071, Playwright): with JavaScript disabled screenshots work but EvaluateAsync does not
        // Screenshots work with page JavaScript disabled; page.EvaluateAsync does not.
        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            JavaScriptEnabled = false,
            ColorScheme = ColorScheme.Dark
        });
        // END WORKAROUND(W-071)
        var page = await context.NewPageAsync();
        await page.GotoAsync(BaseUrl);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // The content area: a real <main> once the R2.2 shell lands, MudBlazor's
        // MudMainContent div until then (it renders a div, not a main element).
        var main = page.Locator("main, .mud-main-content").First;
        await main.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });

        var box = await main.BoundingBoxAsync();
        Assert.That(box, Is.Not.Null, "The main element has no bounding box.");

        // A 1x1 px screenshot inside main's left padding, below the fixed top bar and
        // within the viewport screenshot: page background, not a card or text.
        var viewport = page.ViewportSize;
        var x = box!.X + 4;
        var y = Math.Min(box.Y + 120, box.Y + box.Height - 4);
        y = Math.Min(y, (viewport?.Height ?? 720) - 1);
        var png = await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Clip = new Clip { X = x, Y = y, Width = 1, Height = 1 },
            Type = ScreenshotType.Png
        });

        var (r, g, b) = PngPixelDecoder.PixelAt(png);
        Assert.That(r, Is.EqualTo(0x0F).Within(2), $"Red channel of page background is #{r:X2}{g:X2}{b:X2}, expected #0F172A.");
        Assert.That(g, Is.EqualTo(0x17).Within(2), $"Green channel of page background is #{r:X2}{g:X2}{b:X2}, expected #0F172A.");
        Assert.That(b, Is.EqualTo(0x2A).Within(2), $"Blue channel of page background is #{r:X2}{g:X2}{b:X2}, expected #0F172A.");
    }

    [Test]
    public async Task ExampleBranding_RootPrimaryToken_IsTheExamplePrimary()
    {
        // The generated theme.css carries the site's palette, not hard-coded colours: on the
        // example site (start-local.sh points Blog__BrandingPath at branding.example) the
        // computed --primary must be the example's light primary, #9A3412.
        await Page.GotoAsync(BaseUrl);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var primary = await Page.EvaluateAsync<string>(
            "() => getComputedStyle(document.documentElement).getPropertyValue('--primary').trim()");

        Assert.That(primary, Is.EqualTo("#9A3412"),
            "theme.css is generated from branding.example's palette (its light primary).");
    }

    [Test]
    public async Task NoThirdPartyRequests()
    {
        var slug = await EnsureThemeTestPostAsync();

        // Record every request the browser makes on the public Home and Post pages.
        var urls = new List<string>();
        Page.Request += (_, request) =>
        {
            lock (urls)
            {
                urls.Add(request.Url);
            }
        };

        await Page.GotoAsync(BaseUrl);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Page.GotoAsync($"{BaseUrl}/post/{slug}");
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Theme Test Post" })).ToBeVisibleAsync();
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        List<string> recorded;
        lock (urls)
        {
            recorded = [.. urls];
        }

        Assert.That(recorded, Is.Not.Empty, "No requests were recorded on Home and Post.");

        // The outcome the visitor gets: everything the page loads comes from this host.
        var foreign = recorded
            .Where(u => !Uri.TryCreate(u, UriKind.Absolute, out var uri) || uri.Host != "localhost")
            .Select(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) ? uri.Host : u)
            .Distinct()
            .ToList();
        Assert.That(foreign, Is.Empty, $"Pages requested third-party hosts: {string.Join(", ", foreign)}");
    }

    [Test]
    public async Task ThemeSwitch_PersistsLight_AcrossReload_EvenWhenSystemDark()
    {
        // Ruling R2-R2: this test is part of Task R2.2's commit — the UI it clicks (ThemeSwitch)
        // and the theme cycling in wwwroot/js/shell.js are this task's output.
        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ColorScheme = ColorScheme.Dark
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync(BaseUrl);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Click the switch until it shows "Light" (it cycles System -> Light -> Dark).
        var themeSwitch = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Theme: " });
        for (var i = 0; i < 3; i++)
        {
            if (await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Theme: Light", Exact = true }).CountAsync() > 0)
            {
                break;
            }

            var before = (await themeSwitch.TextContentAsync())?.Trim();
            await themeSwitch.ClickAsync();
            await page.WaitForFunctionAsync(
                "prev => { const b = document.querySelector('.theme-switch'); return b !== null && b.textContent.trim() !== prev; }",
                before);
        }

        await Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Theme: Light", Exact = true }))
            .ToBeVisibleAsync();

        await page.ReloadAsync();
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var main = page.Locator("main").First;
        await main.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });
        var box = await main.BoundingBoxAsync();
        Assert.That(box, Is.Not.Null, "The main element has no bounding box.");

        // A 1x1 px screenshot inside main's left padding: page background, not a card or text.
        // The stored choice must win over the system's dark scheme: #F8FAFC (the light --bg;
        // branding.example leaves bg to the built-in defaults).
        var viewport = page.ViewportSize;
        var x = box!.X + 4;
        var y = Math.Min(box.Y + 120, box.Y + box.Height - 4);
        y = Math.Min(y, (viewport?.Height ?? 720) - 1);
        var png = await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Clip = new Clip { X = x, Y = y, Width = 1, Height = 1 },
            Type = ScreenshotType.Png
        });

        var (r, g, b) = PngPixelDecoder.PixelAt(png);
        Assert.That(r, Is.EqualTo(0xF8).Within(2), $"Red channel of page background is #{r:X2}{g:X2}{b:X2}, expected #F8FAFC.");
        Assert.That(g, Is.EqualTo(0xFA).Within(2), $"Green channel of page background is #{r:X2}{g:X2}{b:X2}, expected #F8FAFC.");
        Assert.That(b, Is.EqualTo(0xFC).Within(2), $"Blue channel of page background is #{r:X2}{g:X2}{b:X2}, expected #F8FAFC.");
    }

    /// <summary>
    /// Task R3.0 item 2 (ruling R3-R0-a): in dark mode a text-variant MudButton must be readable
    /// crimson text — its computed colour needs WCAG contrast ≥ 4.5:1 against <c>--surface</c>.
    /// The probe is the staff-side not-found page's Home/Search MudButtons (an unknown draft slug: the
    /// public not-found page is static and has none) (the rebuilt cards have no
    /// buttons and Task R3.3 removes the "Read" text buttons). Before the palette fix
    /// PaletteDark.Primary was #7A2140 (~1.7:1 against the dark --surface).
    /// </summary>
    [Test]
    public async Task DarkMode_TextButton_ContrastsAtLeast45AgainstSurface()
    {
        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new() { Width = 1280, Height = 800 }
        });
        await context.AddInitScriptAsync("localStorage.setItem('blog-theme', 'dark')");
        var page = await context.NewPageAsync();
        await LoginAsync(page, "admin@example.com", "password");
        await page.GotoAsync($"{BaseUrl}/editor/posts/no-such-draft-{Guid.NewGuid():N}");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // MainLayout flips MudThemeProvider's IsDarkMode after first render. Wait for the dark
        // palette to be applied — its Background (#0F172A) is the dark --bg the branding
        // palette carries, so this wait resolves once theme.css and BlogTheme agree.
        await page.WaitForFunctionAsync(
            @"() => {
                const v = getComputedStyle(document.documentElement).getPropertyValue('--mud-palette-background');
                const m = v.match(/rgba?\((\d+),\s*(\d+),\s*(\d+)/);
                return m !== null && +m[1] === 15 && +m[2] === 23 && +m[3] === 42;
            }");

        var buttons = page.Locator("main a.mud-button-root.mud-button-text-primary");
        var count = await buttons.CountAsync();
        Assert.That(count, Is.EqualTo(2),
            "The not-found page must host the two text-variant MudButtons (Home, Search) — ruling R3-R0-a.");

        var surface = await page.EvaluateAsync<string>(@"() => {
            const probe = document.createElement('div');
            probe.style.backgroundColor = 'var(--surface)';
            document.body.appendChild(probe);
            const colour = getComputedStyle(probe).backgroundColor;
            probe.remove();
            return colour;
        }");

        for (var i = 0; i < count; i++)
        {
            var colour = await buttons.Nth(i).EvaluateAsync<string>("el => getComputedStyle(el).color");
            var ratio = ContrastRatio(colour, surface);
            Assert.That(ratio, Is.GreaterThanOrEqualTo(4.5),
                $"The not-found text button {i + 1} colour {colour} contrasts {ratio:0.00}:1 against --surface {surface}, expected >= 4.5:1.");
        }
    }

    /// <summary>
    /// R2 final-review hand-off (Task R3.0): the old fixture published a fresh "Theme Test Post"
    /// on every run and accumulated in the shared dev DB. Reuse the already-published post when
    /// it exists (the idempotent LayoutTests fixture pattern); publish only when it is missing.
    /// Returns the post's slug; the assertions of the tests using it are unchanged.
    /// </summary>
    private async Task<string> EnsureThemeTestPostAsync()
    {
        if (_themePostSlug is not null)
        {
            return _themePostSlug;
        }

        await DataGate.WaitAsync();
        try
        {
            if (_themePostSlug is not null)
            {
                return _themePostSlug;
            }

            // The public list search narrows to posts containing the three words; the exact title
            // decides. Only published posts are listed for anonymous callers.
            await using (var posts = await Page.APIRequest.GetAsync($"{ApiUrl}/api/posts?searchTerm=Theme%20Test%20Post&pageSize=50"))
            {
                Assert.That(posts.Status, Is.EqualTo(200), "The API's post list is not reachable on :5000.");
                using var doc = JsonDocument.Parse(await posts.TextAsync());
                foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
                {
                    if (item.GetProperty("title").GetString() == "Theme Test Post")
                    {
                        _themePostSlug = item.GetProperty("slug").GetString();
                        return _themePostSlug!;
                    }
                }
            }

            await LoginAsync("admin@example.com", "password");

            // Route/selector migration (rule 5-sanctioned): /admin → /admin/posts/new,
            // "Publish / Schedule" → "Publish now", and the slug comes from the API by id.
            await OpenNewPostEditorAsync();

            await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync("Theme Test Post");
            await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).BlurAsync();
            await Page.GetByLabel("Markdown").FillAsync("Theme test post content.");
            await Page.GetByLabel("Markdown").BlurAsync();

            await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Publish now" }).ClickAsync();
            await Page.WaitForURLAsync(
                url => url.StartsWith($"{BaseUrl}/admin/posts/") && url.EndsWith("/edit"),
                new PageWaitForURLOptions { Timeout = 15000 });
            _themePostSlug = await SlugForPostAsync(PostIdFromEditorUrl());

            await SignOutAsync();
            return _themePostSlug;
        }
        finally
        {
            DataGate.Release();
        }
    }

    /// <summary>
    /// WCAG 2.x contrast ratio of a computed text colour on a computed background, alpha-aware
    /// (<see cref="ColorContrast"/>): a semi-transparent text colour is drawn over the surface it sits
    /// on before the ratio is taken, and a semi-transparent surface over the dark page background
    /// (#0F172A) it lies on. Reading only the rgb channels rates such a colour as if it were opaque.
    /// </summary>
    private static double ContrastRatio(string foreground, string background)
    {
        var surface = ColorContrast.BackdropOf([background], new ColorContrast.Rgba(15, 23, 42, 1));
        var text = ColorContrast.Composite(ColorContrast.Parse(foreground), surface);
        return ColorContrast.Ratio(text, surface);
    }
}
