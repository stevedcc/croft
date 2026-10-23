using System.Globalization;

namespace Croft.Web;

/// <summary>
/// Parses the colour syntax a site's branding may use: <c>#RGB</c>, <c>#RRGGBB</c>,
/// <c>#RRGGBBAA</c>, <c>rgb(r,g,b)</c> and <c>rgba(r,g,b,a)</c>; anything else is invalid.
/// Channels are 0-255, the alpha a number between 0 and 1. Values are normalised to
/// (r, g, b, a) doubles between 0 and 1 for the contrast maths.
/// </summary>
public static class BrandingColor
{
    public static bool TryParse(string value, out double r, out double g, out double b, out double a)
    {
        r = g = b = a = 0;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (value.StartsWith('#'))
        {
            var hex = value[1..];
            if (hex.Length is not (3 or 6 or 8) || !hex.All(Uri.IsHexDigit))
            {
                return false;
            }

            if (hex.Length == 3)
            {
                hex = string.Concat(hex.Select(c => $"{c}{c}"));
            }

            r = int.Parse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            g = int.Parse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            b = int.Parse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            a = hex.Length == 8
                ? int.Parse(hex[6..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0
                : 1.0;
            return true;
        }

        if ((value.StartsWith("rgb(", StringComparison.Ordinal) || value.StartsWith("rgba(", StringComparison.Ordinal))
            && value.EndsWith(')'))
        {
            var parts = value[(value.IndexOf('(') + 1)..^1].Split(',');
            var wantsAlpha = value.StartsWith("rgba", StringComparison.Ordinal);
            if (parts.Length != (wantsAlpha ? 4 : 3))
            {
                return false;
            }

            if (!parts.Take(3).All(p => int.TryParse(p.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) && c is >= 0 and <= 255))
            {
                return false;
            }

            r = int.Parse(parts[0].Trim(), CultureInfo.InvariantCulture) / 255.0;
            g = int.Parse(parts[1].Trim(), CultureInfo.InvariantCulture) / 255.0;
            b = int.Parse(parts[2].Trim(), CultureInfo.InvariantCulture) / 255.0;

            if (wantsAlpha)
            {
                if (!double.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var alpha)
                    || alpha is < 0 or > 1)
                {
                    return false;
                }

                a = alpha;
            }
            else
            {
                a = 1.0;
            }

            return true;
        }

        return false;
    }

    /// <summary>Whether the colour lets whatever is behind it show through (alpha below 1).</summary>
    public static bool IsTranslucent(string value) =>
        TryParse(value, out _, out _, out _, out var a) && a < 1.0;
}
