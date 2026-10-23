namespace Croft.Web.Tests;

/// <summary>
/// app.css's marked scrollbar region: MudBlazor's scrollbar rules, copied verbatim from the
/// style block MudThemeProvider renders (MainLayout turns that block off with DefaultScrollbar
/// "true"). The markers keep that copy recognisable — the byte-exactness of the region is
/// MudThemeStyleCspTests' assertion — and it is one of the marked parts of app.css allowed to
/// carry colour values (the other is ScrimColourValues' region).
/// </summary>
internal static class MudBlazorScrollbarCopy
{
    public const string BeginMarker = "/* mudblazor-scrollbar-copy:begin */";
    public const string EndMarker = "/* mudblazor-scrollbar-copy:end */";

    /// <summary>
    /// Splits app.css into the copied region between the markers and the rest of the file.
    /// Without both markers in order the region is empty and the rest is the whole file: the
    /// verbatim-copy test then fails loudly and the colour ban checks everything.
    /// </summary>
    public static (string Region, string Remainder) Split(string appCss)
    {
        var begin = appCss.IndexOf(BeginMarker, StringComparison.Ordinal);
        var end = appCss.IndexOf(EndMarker, StringComparison.Ordinal);
        if (begin < 0 || end <= begin)
        {
            return (string.Empty, appCss);
        }

        var start = begin + BeginMarker.Length;
        return (appCss[start..end], appCss[..begin] + appCss[(end + EndMarker.Length)..]);
    }
}
