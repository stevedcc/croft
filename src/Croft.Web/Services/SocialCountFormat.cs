using System.Globalization;

namespace Croft.Web.Services;

/// <summary>The short form of a like, repost or reply count on the overlay pill (brief 17e).</summary>
public static class SocialCountFormat
{
    /// <summary>
    /// Below 1 000 the number itself; below 1 000 000 thousands, from there millions, with one decimal and a trailing <c>.0</c> dropped
    /// (<c>1k</c>, <c>1.2k</c>, <c>63.7k</c>, <c>1M</c>), rounded half away from zero, in the invariant culture.
    /// </summary>
    public static string Compact(int count)
    {
        long n = Math.Max(count, 0);
        if (n < 1_000)
            return n.ToString(CultureInfo.InvariantCulture);

        // Integer arithmetic: tenths of a thousand (or of a million), rounded half up (the count is never negative).
        var thousandsTenths = (n + 50) / 100;
        if (thousandsTenths < 10_000)
            return Scaled(thousandsTenths, "k");

        return Scaled((n + 50_000) / 100_000, "M");
    }

    private static string Scaled(long tenths, string suffix) =>
        tenths % 10 == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{tenths / 10}{suffix}")
            : string.Create(CultureInfo.InvariantCulture, $"{tenths / 10}.{tenths % 10}{suffix}");
}
