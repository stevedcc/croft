using static Croft.Web.E2E.ColorContrast;

namespace Croft.Web.E2E;

/// <summary>
/// Brief 10b item 4: a fresh Croft install shows the Black Watch tartan as its fallback banner.
/// Runs only against a site with NO branding folder (dev/run-e2e.sh --default-branding, which the
/// plain dev/run-e2e.sh also does after the main suite): instances normally run on
/// branding.example, whose own banner would be what the card shows. A banner-less post's list card
/// is rendered, its fallback is photographed and three pixels inside one sett are sampled: the
/// crossings where warp and weft are both green, both black and both dark blue are solid colour
/// whatever the twill does, and must equal the colour under the fallback wash. A crossing of black
/// warp and green weft is a twill mix: its green channel lies between the two pure colours.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
[Category("DefaultBranding")]
public class DefaultBannerPixelTests : StaticReadingTest
{
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];

    /// <summary>The tile is 416 threads (units) square, drawn 180px wide (background-size).</summary>
    private const double PixelsPerUnit = 180.0 / 416.0;

    // Offsets (units) inside one sett, from the thread count DB44 K4 DB4 K4 DB4 K32 G32 K4 G32
    // K32 DB32 K4 DB4 K4 DB32 K32 G32 K4 G32 K32 DB4 K4 DB4 K4: green 92-124, black 160-192,
    // dark blue 192-224.
    private const double GreenAt = 108;
    private const double BlackAt = 176;
    private const double BlueAt = 208;

    private static readonly Rgba DarkBlue = Parse("rgb(28, 40, 65)");
    private static readonly Rgba Black = Parse("rgb(14, 14, 16)");
    private static readonly Rgba Green = Parse("rgb(31, 71, 48)");

    [Test]
    public async Task TheDefaultFallback_OnAListCard_ShowsTheBlackWatchColours()
    {
        await PublishAsync($"Tartan older {RunId}", BodyWithSections(RunId, 1));
        await PublishAsync($"Tartan newest {RunId}", BodyWithSections(RunId, 1));

        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new() { Width = 1440, Height = 900 },
            ColorScheme = ColorScheme.Light,
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync(BaseUrl);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var card = page.Locator(".post-card--list", new PageLocatorOptions { HasText = $"Tartan older {RunId}" });
        var fallback = card.Locator(".banner-or-fallback__fallback");
        await Expect(fallback).ToBeVisibleAsync();

        // The site has no branding folder, so the card paints the framework's own file.
        Assert.That(await fallback.EvaluateAsync<string>("el => getComputedStyle(el).backgroundImage"),
            Does.Contain("/branding/fallback-banner.svg"));

        // A list card's tartan is plain: its category chip shows the icon (brief 10b item 6).
        await Expect(card.Locator(".banner-or-fallback__icon")).ToHaveCountAsync(0);

        // Photograph the fallback.
        var wash = Parse(await fallback.EvaluateAsync<string>("el => getComputedStyle(el, '::before').backgroundColor"));
        var shot = PngPixelDecoder.Decode(await fallback.ScreenshotAsync(new LocatorScreenshotOptions { Type = ScreenshotType.Png }));
        Assert.That(shot.Height, Is.GreaterThan(PixelsPerUnit * (BlueAt + 16)), "the media box shows the sett's first 224 units");

        Rgba At(double warpUnit, double weftUnit)
        {
            var x = (int)Math.Round(weftUnit * PixelsPerUnit);
            var y = (int)Math.Round(warpUnit * PixelsPerUnit);
            var i = (y * shot.Width + x) * 4;
            return new Rgba(shot.Rgba[i], shot.Rgba[i + 1], shot.Rgba[i + 2], 1);
        }

        void AssertColour(string name, Rgba actual, Rgba thread)
        {
            var expected = Composite(wash, thread);
            Assert.That(actual.R, Is.EqualTo(expected.R).Within(4), $"{name}: red, got rgb({actual.R}, {actual.G}, {actual.B}) expected about rgb({expected.R:0}, {expected.G:0}, {expected.B:0})");
            Assert.That(actual.G, Is.EqualTo(expected.G).Within(4), $"{name}: green channel");
            Assert.That(actual.B, Is.EqualTo(expected.B).Within(4), $"{name}: blue channel");
        }

        AssertColour("green x green", At(GreenAt, GreenAt), Green);
        AssertColour("black x black", At(BlackAt, BlackAt), Black);
        AssertColour("dark blue x dark blue", At(BlueAt, BlueAt), DarkBlue);

        // Black warp across green weft: the twill mixes them, so green lies between the two pure colours.
        var mix = At(BlackAt, GreenAt);
        var (low, high) = (Composite(wash, Black).G, Composite(wash, Green).G);
        Assert.That(mix.G, Is.GreaterThan(low + 4).And.LessThan(high - 4),
            $"black x green is a twill mix: green channel {mix.G} between {low:0} (black) and {high:0} (green)");
    }
}
