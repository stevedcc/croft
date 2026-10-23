namespace Croft.Web.Tests;

/// <summary>
/// app.css's marked error-text region (W-095): the three <c>--error-text</c> values that make
/// MudBlazor's text-variant error buttons reach WCAG AA, dialogs included. Fixed design constants
/// like the scrim's alphas, not brandable palette entries, and not in the MudTheme (a different
/// palette changes the theme's inline style and with it the CSP's hash). The markers keep the
/// carve-out explicit: BrandingThemeCssTests' colour ban allow-lists this region, the scrim's and
/// MudBlazorScrollbarCopy's.
/// </summary>
internal static class ErrorTextColourValues
{
    public const string BeginMarker = "/* error-text-colour-values:begin */";
    public const string EndMarker = "/* error-text-colour-values:end */";

    /// <summary>
    /// Splits app.css into the region between the markers and the rest of the file. Without both
    /// markers in order the region is empty and the rest is the whole file, so the colour ban then
    /// checks the error-text blocks too and fails loudly.
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
