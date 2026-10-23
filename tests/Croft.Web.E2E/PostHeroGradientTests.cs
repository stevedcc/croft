using System.Text.Json;

namespace Croft.Web.E2E;

/// <summary>
/// The hero's dark overlay follows its text instead of the hero's height. The old overlay
/// (BannerOrFallback's fixed gradient over the bottom 65% of the hero) leaves a long title's top
/// lines on the bare banner: a long title grows the hero (brief 03l), so the gradient's fixed
/// band no longer reaches the text. Outcomes asserted on a pure white 1600x900 banner, where the
/// rendered pixel is exactly 255 × (1 − alpha): the pixel at the title's first line (its top edge
/// is the worst place on the hero) is sRGB ≤ 118 per channel — the darkest backdrop white text
/// can still read at 4.5:1 — for titles of 20, 120 and 300 characters at 2560x1440, 1440x900 and
/// 390x844, and the pixel 4rem + 8px above the text block's top (the fade has ended) shows the
/// bare banner again (≥ 245 per channel) wherever that point is still inside the hero. The
/// title's and subtitle's colour is transparent while sampling, so no glyph can darken the
/// pixel. Break-it-once: restoring the fixed 65% gradient fails the 300-character sample at
/// 2560x1440 (the first line then sits where the old gradient's alpha is 0).
///
/// Three fixture posts, run-keyed unique, published through the admin UI once per run (the
/// suite's rule), each with the same subtitle and the same uploaded pure white banner.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class PostHeroGradientTests : WebTest
{
    private static readonly SemaphoreSlim DataGate = new(1, 1);
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..8];

    /// <summary>The subtitle every fixture hero shows (the hero's text block is never just a title).</summary>
    private const string Subtitle = "A subtitle for the hero-gradient fixtures, under every title";

    private static bool _dataReady;
    private static string _slug20 = string.Empty;
    private static string _slug120 = string.Empty;
    private static string _slug300 = string.Empty;

    /// <summary>The title's first line must sit on a backdrop dark enough for white text: every
    /// channel of the sampled pixel ≤ 118 (white text needs 4.5:1, i.e. sRGB ≤ 118 on white).</summary>
    [TestCase(20, 2560, 1440)]
    [TestCase(20, 1440, 900)]
    [TestCase(20, 390, 844)]
    [TestCase(120, 2560, 1440)]
    [TestCase(120, 1440, 900)]
    [TestCase(120, 390, 844)]
    [TestCase(300, 2560, 1440)]
    [TestCase(300, 1440, 900)]
    [TestCase(300, 390, 844)]
    public async Task TitleFirstLine_SitsOnADarkEnoughBackdropForWhiteText(int titleLength, int width, int height)
    {
        await EnsureFixtureAsync();
        await ShowPostAsync(titleLength, width, height);

        // The report's before/after captures (20- and 300-character heroes at 1440x900) are taken
        // with the text in place, before the sampling runs make it transparent.
        if (width == 1440 && titleLength is 20 or 300)
        {
            await SaveHeroCaptureAsync(titleLength);
        }

        var g = await MeasureAsync();
        await HideGlyphsAsync();

        var y = (int)Math.Round((g.FirstLineTop + g.FirstLineBottom) / 2);
        var (r, gr, b) = await SamplePixelAsync(g.SampleX, y);
        var above = g.HeroBottom - g.FirstLineTop;
        TestContext.Progress.WriteLine(
            $"hero-gradient {titleLength}ch @{width}x{height}: hero height {g.HeroHeight}px, title first-line top {above:0.#}px above the hero's bottom ({above / g.HeroHeight:0.0%} of its height); " +
            $"sample ({g.SampleX},{y}) rgb({r},{gr},{b}).");

        var darkest = Math.Max(r, Math.Max(gr, b));
        Assert.That(darkest, Is.LessThanOrEqualTo(118),
            $"The backdrop behind the {titleLength}-character title's first line at {width}x{height} is rgb({r},{gr},{b}) " +
            $"at ({g.SampleX},{y}): {above:0.#}px, or {above / g.HeroHeight:0.0%}, up the hero (height {g.HeroHeight}px). " +
            "White hero text needs a backdrop of sRGB <= 118 per channel (4.5:1 on white), so no channel may exceed 118.");
    }

    /// <summary>4rem above the text block the fade has ended: the bare banner shows again, every
    /// channel of the sampled pixel ≥ 245. The point is only sampled where it is still inside the
    /// hero — a hero that grew with its text pushes the fade end off its top edge, and then the
    /// condition does not apply (the test says so rather than passing silently).</summary>
    [TestCase(20, 2560, 1440)]
    [TestCase(20, 1440, 900)]
    [TestCase(20, 390, 844)]
    [TestCase(120, 2560, 1440)]
    [TestCase(120, 1440, 900)]
    [TestCase(120, 390, 844)]
    [TestCase(300, 2560, 1440)]
    [TestCase(300, 1440, 900)]
    [TestCase(300, 390, 844)]
    public async Task AboveTheTextBlock_TheFadeEndsAndTheBannerShows(int titleLength, int width, int height)
    {
        await EnsureFixtureAsync();
        await ShowPostAsync(titleLength, width, height);
        var g = await MeasureAsync();

        // 4rem is not 64px at every width: the root font size is fluid, so rem is measured.
        var y = (int)Math.Round(g.ContentTop - 4 * g.RootFontPx - 8);
        if (y <= g.HeroTop)
        {
            Assert.Ignore(
                $"The fade end (y={y}) is {g.HeroTop - y:0.#}px above the hero's top (y={g.HeroTop}) for the {titleLength}-character hero at {width}x{height} " +
                $"(hero height {g.HeroHeight}px, text block top y={g.ContentTop}, 4rem = {4 * g.RootFontPx:0.#}px): " +
                "the hero grew with its text, so 4rem + 8px above the text block is not inside the hero and the condition does not apply.");
        }

        await HideGlyphsAsync();
        var (r, gr, b) = await SamplePixelAsync(g.SampleX, y);
        TestContext.Progress.WriteLine(
            $"hero-gradient fade-end {titleLength}ch @{width}x{height}: text block top y={g.ContentTop}, 4rem={4 * g.RootFontPx:0.#}px, " +
            $"hero top y={g.HeroTop}; sample ({g.SampleX},{y}) rgb({r},{gr},{b}).");

        var lightest = Math.Min(r, Math.Min(gr, b));
        Assert.That(lightest, Is.GreaterThanOrEqualTo(245),
            $"The pixel ({g.SampleX},{y}), 4rem + 8px above the {titleLength}-character hero's text block top (y={g.ContentTop}) at {width}x{height}, " +
            $"is rgb({r},{gr},{b}): the fade must have ended there and the bare white banner must show (every channel >= 245).");
    }

    /// <summary>One hero's measured geometry: its box, the text block's top, the sample column's
    /// x (the title box's left + 2px), the title's first line box (its vertical middle is the
    /// sample row) and the root font size — 4rem is fluid, so the tests compute it.</summary>
    private sealed record HeroGeometry(
        int HeroTop,
        int HeroBottom,
        int HeroHeight,
        int ContentTop,
        int SampleX,
        double FirstLineTop,
        double FirstLineBottom,
        double RootFontPx);

    /// <summary>Opens the fixture post at one viewport and waits for its hero's text to be laid
    /// out. Callers await EnsureFixtureAsync() first: the slug is read there.</summary>
    private async Task ShowPostAsync(int titleLength, int width, int height)
    {
        await Page.SetViewportSizeAsync(width, height);
        await Page.GotoAsync($"{BaseUrl}/post/{SlugFor(titleLength)}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.Locator(".post-hero__title")).ToBeVisibleAsync();
        await Expect(Page.Locator(".post-hero__subtitle")).ToBeVisibleAsync();
        // The white banner under the gradient must really have loaded: the title's backdrop is sampled
        // from pixels, and the fallback tartan that shows when the image fails is dark everywhere, so
        // a banner that did not load makes the "dark enough" check pass with no gradient at all.
        await AssertImageLoadsAsync(Page.Locator(".post-hero .banner-or-fallback__img"));
        await Page.EvaluateAsync("() => document.fonts.ready");
        await Page.EvaluateAsync("() => window.scrollTo(0, 0)");
    }

    private async Task<HeroGeometry> MeasureAsync()
    {
        var json = await Page.EvaluateAsync<string>(
            @"() => {
                const hero = document.querySelector('.post-hero');
                const content = document.querySelector('.post-hero__content');
                const title = document.querySelector('.post-hero__title');
                const range = document.createRange();
                range.selectNodeContents(title);
                const first = range.getClientRects()[0];
                const heroRect = hero.getBoundingClientRect();
                const titleRect = title.getBoundingClientRect();
                return JSON.stringify({
                    heroTop: heroRect.top,
                    heroBottom: heroRect.bottom,
                    contentTop: content.getBoundingClientRect().top,
                    titleLeft: titleRect.left,
                    firstLineTop: first.top,
                    firstLineBottom: first.bottom,
                    rootFontPx: parseFloat(getComputedStyle(document.documentElement).fontSize),
                    scrollY: window.scrollY
                });
            }");

        using var doc = JsonDocument.Parse(json!);
        var r = doc.RootElement;
        Assert.That(r.GetProperty("scrollY").GetDouble(), Is.Zero,
            "The page must not be scrolled when a 1x1 clip is sampled: its coordinates are viewport-relative.");

        return new HeroGeometry(
            (int)Math.Round(r.GetProperty("heroTop").GetDouble()),
            (int)Math.Round(r.GetProperty("heroBottom").GetDouble()),
            (int)Math.Round(r.GetProperty("heroBottom").GetDouble() - r.GetProperty("heroTop").GetDouble()),
            (int)Math.Round(r.GetProperty("contentTop").GetDouble()),
            (int)Math.Round(r.GetProperty("titleLeft").GetDouble()) + 2,
            r.GetProperty("firstLineTop").GetDouble(),
            r.GetProperty("firstLineBottom").GetDouble(),
            r.GetProperty("rootFontPx").GetDouble());
    }

    /// <summary>Transparent glyphs while sampling: the pixel shows the backdrop alone, never a
    /// white letter or its anti-aliased edge (the brief's sampling rule).</summary>
    private async Task HideGlyphsAsync()
    {
        await Page.EvaluateAsync(
            @"() => {
                document.querySelector('.post-hero__title').style.color = 'transparent';
                const sub = document.querySelector('.post-hero__subtitle');
                if (sub) sub.style.color = 'transparent';
            }");
    }

    /// <summary>The pixel at (x, y) in the unscrolled page, from a 1x1 px PNG clip.</summary>
    private async Task<(int R, int G, int B)> SamplePixelAsync(int x, int y)
    {
        var png = await Page.ScreenshotAsync(new PageScreenshotOptions
        {
            Clip = new Clip { X = x, Y = y, Width = 1, Height = 1 },
            Type = ScreenshotType.Png
        });
        return PngPixelDecoder.PixelAt(png);
    }

    /// <summary>The hero as the reader sees it (text in place), for the item's report captures.</summary>
    private async Task SaveHeroCaptureAsync(int titleLength)
    {
        var path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory, "screenshots", $"hero-gradient-{titleLength}-1440x900.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var png = await Page.Locator(".post-hero").ScreenshotAsync(new LocatorScreenshotOptions { Type = ScreenshotType.Png });
        File.WriteAllBytes(path, png);
    }

    private static string SlugFor(int titleLength) => titleLength switch
    {
        20 => _slug20,
        120 => _slug120,
        300 => _slug300,
        _ => throw new ArgumentOutOfRangeException(nameof(titleLength), $"No fixture post for a {titleLength}-character title.")
    };

    private static void SetSlug(int titleLength, string slug)
    {
        switch (titleLength)
        {
            case 20: _slug20 = slug; break;
            case 120: _slug120 = slug; break;
            case 300: _slug300 = slug; break;
            default: throw new ArgumentOutOfRangeException(nameof(titleLength), $"No fixture post for a {titleLength}-character title.");
        }
    }

    /// <summary>Exactly <paramref name="length"/> characters, run-keyed and length-keyed so the
    /// three fixture slugs can never collide.</summary>
    private static string TitleOf(int length)
    {
        const string Filler = "and the first lines of the title stay on a dark backdrop instead of the bare white banner ";
        var text = $"Gradient {RunId} {length}ch: " + Filler + Filler + Filler + Filler;
        return text[..length];
    }

    /// <summary>A pure white 1600x900 banner: the sampled pixel's arithmetic (255 × (1 − alpha))
    /// only holds on white, so the fixtures upload white rather than a photo. A blank page's
    /// screenshot is exactly that — no image library needed to encode one.</summary>
    private async Task<byte[]> WhiteBannerAsync()
    {
        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new() { Width = 1600, Height = 900 }
        });
        var page = await context.NewPageAsync();
        await page.SetContentAsync("<style>html, body { margin: 0; background: #FFFFFF; }</style>");
        return await page.ScreenshotAsync(new PageScreenshotOptions { Type = ScreenshotType.Png });
    }

    /// <summary>Three published posts (20-, 120- and 300-character titles), each with the same
    /// subtitle and the run's white banner, created through the admin UI once per run.</summary>
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

            var banner = await WhiteBannerAsync();
            await LoginAsync("admin@example.com", "password");

            foreach (var length in new[] { 20, 120, 300 })
            {
                await OpenNewPostEditorAsync();

                await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync(TitleOf(length));
                await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).BlurAsync();
                await Page.GetByLabel("Subtitle").FillAsync(Subtitle);
                await Page.GetByLabel("Subtitle").BlurAsync();
                await Page.GetByLabel("Markdown").FillAsync($"Hero gradient fixture {RunId}, {length}-character title, a short body.");
                await Page.GetByLabel("Markdown").BlurAsync();

                var bannerName = $"hero-gradient-{RunId}-{length}.png";
                await Page.GetByLabel("Upload banner").SetInputFilesAsync(new FilePayload
                {
                    Name = bannerName,
                    MimeType = "image/png",
                    Buffer = banner
                });
                await Expect(Page.GetByText(bannerName).First).ToBeVisibleAsync();

                await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Publish now" }).ClickAsync();
                await Page.WaitForURLAsync(
                    url => url.StartsWith($"{BaseUrl}/admin/posts/") && url.EndsWith("/edit"),
                    new PageWaitForURLOptions { Timeout = 30000 });
                SetSlug(length, await SlugForPostAsync(PostIdFromEditorUrl()));
            }

            await Page.GotoAsync($"{BaseUrl}/logout");
            _dataReady = true;
        }
        finally
        {
            DataGate.Release();
        }
    }
}
