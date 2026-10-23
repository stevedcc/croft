using System.Globalization;
using System.Text.RegularExpressions;

namespace Croft.Web.E2E;

/// <summary>
/// WCAG contrast from the browser's computed colours, alpha-aware (brief 10b item 5): a
/// semi-transparent background is composited over what lies beneath it before any ratio is taken,
/// because reading only its rgb channels reports the colour it would have if it were opaque.
/// Shared by the keyboard-focus and the masthead contrast tests.
/// </summary>
internal static partial class ColorContrast
{
    /// <summary>An sRGB colour, channels 0..255, alpha 0..1.</summary>
    internal readonly record struct Rgba(double R, double G, double B, double A);

    /// <summary>Reads a computed colour: rgb(), rgba() or color(srgb …), comma or space separated.</summary>
    internal static Rgba Parse(string css)
    {
        var legacy = LegacyRgb().Match(css);
        if (legacy.Success)
        {
            return new Rgba(Num(legacy.Groups[1]), Num(legacy.Groups[2]), Num(legacy.Groups[3]),
                legacy.Groups[4].Success ? Num(legacy.Groups[4]) : 1);
        }

        var srgb = SrgbFunction().Match(css);
        if (srgb.Success)
        {
            return new Rgba(Num(srgb.Groups[1]) * 255, Num(srgb.Groups[2]) * 255, Num(srgb.Groups[3]) * 255,
                srgb.Groups[4].Success ? Num(srgb.Groups[4]) : 1);
        }

        var oklab = Oklab().Match(css);
        if (oklab.Success)
        {
            return FromOklab(Percent(oklab.Groups[1], 1), Num(oklab.Groups[2]), Num(oklab.Groups[3]),
                oklab.Groups[4].Success ? Percent(oklab.Groups[4], 1) : 1);
        }

        var oklch = Oklch().Match(css);
        if (oklch.Success)
        {
            var (chroma, hue) = (Num(oklch.Groups[2]), Num(oklch.Groups[3]) * Math.PI / 180);
            return FromOklab(Percent(oklch.Groups[1], 1), chroma * Math.Cos(hue), chroma * Math.Sin(hue),
                oklch.Groups[4].Success ? Percent(oklch.Groups[4], 1) : 1);
        }

        throw new FormatException($"cannot read the computed colour '{css}'");
    }

    /// <summary>Draws <paramref name="top"/> over the opaque <paramref name="below"/>.</summary>
    internal static Rgba Composite(Rgba top, Rgba below) => new(
        top.R * top.A + below.R * (1 - top.A),
        top.G * top.A + below.G * (1 - top.A),
        top.B * top.A + below.B * (1 - top.A),
        1);

    /// <summary>The opaque colour seen behind an element: <paramref name="ancestorBackgrounds"/>
    /// (nearest first) are walked up to the first opaque one, then composited back down over it.
    /// With none opaque, the canvas is taken as <paramref name="canvas"/>.</summary>
    internal static Rgba BackdropOf(IReadOnlyList<string> ancestorBackgrounds, Rgba canvas)
    {
        var layers = ancestorBackgrounds.Select(Parse).ToList();
        var firstOpaque = layers.FindIndex(l => l.A >= 1);
        var backdrop = firstOpaque >= 0 ? layers[firstOpaque] : canvas;
        for (var i = (firstOpaque >= 0 ? firstOpaque : layers.Count) - 1; i >= 0; i--)
        {
            backdrop = Composite(layers[i], backdrop);
        }

        return backdrop with { A = 1 };
    }

    /// <summary>WCAG contrast ratio of two opaque colours.</summary>
    internal static double Ratio(Rgba a, Rgba b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    internal static double Luminance(Rgba c)
    {
        static double Channel(double value)
        {
            var v = value / 255;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    // Björn Ottosson's Oklab → linear sRGB, then the sRGB transfer curve. Chrome serialises
    // color-mix() results (and anything in a wide gamut) in Oklab, so the tests read them too.
    private static Rgba FromOklab(double l, double a, double b, double alpha)
    {
        var l_ = Math.Pow(l + 0.3963377774 * a + 0.2158037573 * b, 3);
        var m_ = Math.Pow(l - 0.1055613458 * a - 0.0638541728 * b, 3);
        var s_ = Math.Pow(l - 0.0894841775 * a - 1.2914855480 * b, 3);
        static double Encode(double linear)
        {
            var c = Math.Clamp(linear, 0, 1);
            return 255 * (c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055);
        }

        return new Rgba(
            Encode(4.0767416621 * l_ - 3.3077115913 * m_ + 0.2309699292 * s_),
            Encode(-1.2684380046 * l_ + 2.6097574011 * m_ - 0.3413193965 * s_),
            Encode(-0.0041960863 * l_ - 0.7034186147 * m_ + 1.7076147010 * s_),
            alpha);
    }

    private static double Percent(Group group, double scale) =>
        double.Parse(group.Value.TrimEnd('%'), CultureInfo.InvariantCulture) / (group.Value.EndsWith('%') ? 100 : 1) * scale;

    private static double Num(Group group) => double.Parse(group.Value, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^rgba?\(\s*([\d.]+)[,\s]+([\d.]+)[,\s]+([\d.]+)(?:\s*[,/]\s*([\d.]+))?\s*\)$")]
    private static partial Regex LegacyRgb();

    [GeneratedRegex(@"^oklab\(\s*([\d.]+%?)\s+(-?[\d.e-]+)\s+(-?[\d.e-]+)(?:\s*/\s*([\d.]+%?))?\s*\)$")]
    private static partial Regex Oklab();

    [GeneratedRegex(@"^oklch\(\s*([\d.]+%?)\s+([\d.e-]+)\s+([\d.e-]+)(?:\s*/\s*([\d.]+%?))?\s*\)$")]
    private static partial Regex Oklch();

    [GeneratedRegex(@"^color\(\s*srgb\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)(?:\s*/\s*([\d.]+))?\s*\)$")]
    private static partial Regex SrgbFunction();
}
