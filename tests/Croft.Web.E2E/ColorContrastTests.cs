using static Croft.Web.E2E.ColorContrast;

namespace Croft.Web.E2E;

/// <summary>The compositing arithmetic the contrast tests stand on, checked without a browser.</summary>
public class ColorContrastTests
{
    private static readonly Rgba White = new(255, 255, 255, 1);

    [Test]
    public void Parse_ReadsLegacyAndSrgbSyntaxWithAlpha()
    {
        Assert.That(Parse("rgb(10, 20, 30)"), Is.EqualTo(new Rgba(10, 20, 30, 1)));
        Assert.That(Parse("rgba(10, 20, 30, 0.5)"), Is.EqualTo(new Rgba(10, 20, 30, 0.5)));
        Assert.That(Parse("rgb(10 20 30 / 0.25)"), Is.EqualTo(new Rgba(10, 20, 30, 0.25)));
        Assert.That(Parse("color(srgb 1 0 0.5 / 0.5)"), Is.EqualTo(new Rgba(255, 0, 127.5, 0.5)));
        Assert.That(Parse("rgba(0, 0, 0, 0)").A, Is.Zero);
    }

    [Test]
    public void Parse_ReadsOklabAndOklch()
    {
        var white = Parse("oklab(1 0 0)");
        Assert.That((white.R, white.G, white.B), Is.EqualTo((255d, 255d, 255d)).Within(0.5));
        var black = Parse("oklab(0 0 0 / 0)");
        Assert.That((black.R, black.A), Is.EqualTo((0d, 0d)));
        var red = Parse("oklch(0.628 0.2577 29.23)"); // sRGB red
        Assert.That((red.R, red.G, red.B), Is.EqualTo((255d, 0d, 0d)).Within(1.5));
    }

    [Test]
    public void Composite_BlendsByAlpha() =>
        Assert.That(Composite(new Rgba(0, 0, 0, 0.5), White), Is.EqualTo(new Rgba(127.5, 127.5, 127.5, 1)));

    [Test]
    public void BackdropOf_WalksUpToTheFirstOpaqueAncestorThenCompositesDown()
    {
        // nearest first: a 50% black wash, a transparent box, an opaque white page
        var backdrop = BackdropOf(["rgba(0, 0, 0, 0.5)", "rgba(0, 0, 0, 0)", "rgb(255, 255, 255)", "rgb(1, 2, 3)"], White);

        Assert.That(backdrop, Is.EqualTo(new Rgba(127.5, 127.5, 127.5, 1)),
            "layers above the first opaque one count, layers beyond it do not");
    }

    [Test]
    public void BackdropOf_WithNoOpaqueAncestor_UsesTheCanvas() =>
        Assert.That(BackdropOf(["rgba(0, 0, 0, 0)"], White), Is.EqualTo(White));

    [Test]
    public void Ratio_OfBlackOnWhite_Is21() =>
        Assert.That(Ratio(new Rgba(0, 0, 0, 1), White), Is.EqualTo(21).Within(0.001));
}
