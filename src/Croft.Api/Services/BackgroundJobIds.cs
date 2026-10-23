namespace Croft.Api.Services;

/// <summary>
/// Stable ids for the background jobs' Postgres advisory locks (one id per job). Every API
/// instance running the same job must contend on the same lock, so these values must stay the
/// same across releases: during a rolling deploy old and new instances run side by side.
/// </summary>
public static class BackgroundJobIds
{
    public const long ScheduledPublisher = 1;
    public const long PostSummaryBackfill = 2;

    /// <summary>The API's "migrate" entry (the k8s init container): one migration chain at a time.</summary>
    public const long Migrations = 3;

    /// <summary>Deleting PageViews rows past the retention window: one replica per run.</summary>
    public const long PageViewRetention = 4;

    /// <summary>Claiming the social threads that are due (brief 12a): one replica claims at a time; the fetching happens outside the lock.</summary>
    public const long SocialFetch = 5;

    /// <summary>Claiming the announcements that are due (brief 12d): one replica claims at a time; the posting happens outside the lock.</summary>
    public const long SocialAnnounce = 6;
}
