namespace Croft.Web.Tests;

/// <summary>
/// app.css's marked scrim region: the contents drawer's fixed alpha constants — black 32% in
/// light, 48% in dark — kept out of theme.css because they are design constants, not brandable
/// palette entries. The markers keep that carve-out explicit: it is one of the marked parts of
/// app.css allowed to carry colour values (BrandingThemeCssTests' ban allow-lists this region
/// and MudBlazorScrollbarCopy's).
/// </summary>
internal static class ScrimColourValues
{
    public const string BeginMarker = "/* scrim-colour-values:begin */";
    public const string EndMarker = "/* scrim-colour-values:end */";

    /// <summary>
    /// Splits app.css into the scrim region between the markers and the rest of the file.
    /// Without both markers in order the region is empty and the rest is the whole file: the
    /// colour ban then checks the scrim blocks too and fails loudly.
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
