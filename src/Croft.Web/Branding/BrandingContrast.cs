namespace Croft.Web;

/// <summary>
/// The WCAG 2 relative-luminance contrast ratio between two colours, used for the startup
/// warnings when a site's palette leaves a checked pair below AA (4.5:1).
/// </summary>
public static class BrandingContrast
{
    /// <summary>The contrast ratio (1.00 for identical colours, 21.00 for black on white).</summary>
    public static double Ratio(string foreground, string background)
    {
        var a = RelativeLuminance(foreground);
        var b = RelativeLuminance(background);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>WCAG 2 relative luminance of a colour (sRGB; its alpha is ignored).</summary>
    public static double RelativeLuminance(string colour)
    {
        if (!BrandingColor.TryParse(colour, out var r, out var g, out var b, out _))
        {
            throw new BrandingException($"\"{colour}\" is not a colour");
        }

        return 0.2126 * Linear(r) + 0.7152 * Linear(g) + 0.0722 * Linear(b);
    }

    private static double Linear(double channel) =>
        channel <= 0.03928 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
}
