using System.Globalization;

namespace Croft.Web;

/// <summary>
/// "just now", "5 minutes ago", "3 hours ago", "2 days ago", then the date "12 Oct 2026" (brief 12b).
/// The discussion sits on a page the output cache keeps for a minute, so "just now" covers under two minutes.
/// A time in the future (clock skew between the platform and this pod) counts as "just now".
/// </summary>
public static class RelativeTime
{
    /// <summary>From this age on, the date replaces "N days ago".</summary>
    private const int DaysBeforeDate = 30;

    public static string Format(DateTimeOffset at, DateTimeOffset now)
    {
        var age = now - at;
        if (age < TimeSpan.FromMinutes(2))
        {
            return "just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return Plural((int)age.TotalMinutes, "minute");
        }

        if (age < TimeSpan.FromDays(1))
        {
            return Plural((int)age.TotalHours, "hour");
        }

        if (age < TimeSpan.FromDays(DaysBeforeDate))
        {
            return Plural((int)age.TotalDays, "day");
        }

        return at.UtcDateTime.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The same for a moment still to come: "in 10 minutes", "in 3 hours", "in 2 days"; "due now" from a minute
    /// before it on (the fetcher checks its threads on a tick, so a due time is never exact) and for any past moment.
    /// </summary>
    public static string FormatUntil(DateTimeOffset at, DateTimeOffset now)
    {
        var wait = at - now;
        if (wait < TimeSpan.FromMinutes(1))
        {
            return "due now";
        }

        if (wait < TimeSpan.FromHours(1))
        {
            return $"in {Quantity((int)wait.TotalMinutes, "minute")}";
        }

        if (wait < TimeSpan.FromDays(1))
        {
            return $"in {Quantity((int)wait.TotalHours, "hour")}";
        }

        return $"in {Quantity((int)wait.TotalDays, "day")}";
    }

    private static string Plural(int count, string unit) => $"{Quantity(count, unit)} ago";

    private static string Quantity(int count, string unit) => $"{count} {unit}{(count == 1 ? string.Empty : "s")}";
}
