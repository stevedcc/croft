namespace Croft.Api.Social;

/// <summary>
/// How often a discussion is fetched (brief 12a): every 10 minutes while it is younger than 2 days, hourly while it is younger
/// than 30 days, daily after that. Its age runs from the later of the post's publication and the link to the announcement.
/// </summary>
public static class SocialSchedule
{
    public static readonly TimeSpan Young = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan Middle = TimeSpan.FromHours(1);
    public static readonly TimeSpan Old = TimeSpan.FromDays(1);

    public static TimeSpan Interval(DateTime anchor, DateTime now)
    {
        var age = now - anchor;
        if (age < TimeSpan.FromDays(2))
            return Young;

        return age < TimeSpan.FromDays(30) ? Middle : Old;
    }
}
