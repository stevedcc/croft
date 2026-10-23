namespace Croft.Web;

/// <summary>
/// Pure helper for combining a date and optional time into a UTC DateTime.
/// Does not depend on TimeZoneInfo.Local or any clock.
/// </summary>
public static class UtcDateTime
{
    /// <summary>
    /// Combines a date and optional time into a UTC DateTime.
    /// If date is null, returns null. If time is null, defaults to midnight.
    /// The resulting DateTime always has Kind=Utc.
    /// </summary>
    public static DateTime? Combine(DateTime? date, TimeSpan? time)
    {
        if (date is null)
            return null;

        var dateOnly = date.Value.Date;
        var combined = dateOnly + (time ?? TimeSpan.Zero);
        return DateTime.SpecifyKind(combined, DateTimeKind.Utc);
    }
}
