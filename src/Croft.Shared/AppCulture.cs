using System.Globalization;

namespace Croft.Shared;

/// <summary>
/// The one place that decides the process culture, called at startup by both apps and from the
/// tests, so the tests exercise exactly the production culture configuration: en-GB (English
/// names, 24-hour time) with the short date pattern forced to ISO 8601 (yyyy-MM-dd). Every
/// culture-dependent short date — MudBlazor's date and time pickers included — is then
/// unambiguous, never US (month-day) or UK (day-month) order. The pattern is changed on a
/// clone: GetCultureInfo hands out a cached read-only instance.
/// </summary>
public static class AppCulture
{
    // WORKAROUND(W-048, .NET): GetCultureInfo returns a cached read-only culture, so it is cloned to set an ISO short date for the MudBlazor pickers
    /// <summary>Sets the default thread cultures for every thread that has no culture of its own.</summary>
    public static void ConfigureDefaults()
    {
        var culture = (CultureInfo)CultureInfo.GetCultureInfo("en-GB").Clone();
        culture.DateTimeFormat.ShortDatePattern = "yyyy-MM-dd";
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }
    // END WORKAROUND(W-048)
}
