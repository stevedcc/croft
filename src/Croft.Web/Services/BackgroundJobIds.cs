namespace Croft.Web.Services;

/// <summary>
/// Stable ids for the Web app's Postgres advisory locks (one id per job), the Web half of the
/// API's job ids. Every Web instance running the same job must contend on the same lock, so
/// these values must stay the same across releases: during a rolling deploy old and new
/// instances run side by side. The values differ from the API's ids so the two apps never
/// contend on the same key.
/// </summary>
public static class BackgroundJobIds
{
    /// <summary>The Web app's "migrate" entry (the k8s init container): one migration chain at a time.</summary>
    public const long Migrations = 100;

    /// <summary>The Web app's expired-session purge: one purge tick at a time across Web instances.</summary>
    public const long SessionPurge = 101;
}
