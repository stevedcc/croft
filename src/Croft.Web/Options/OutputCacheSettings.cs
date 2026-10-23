using System.Globalization;

namespace Croft.Web.Options;

/// <summary>
/// The <c>Croft:Web:OutputCache:*</c> settings (brief 14b): how long an anonymous reader's page is
/// kept in the Web pod's memory, how long the feed and sitemap are, and the cache's bounds.
/// An invalid value is a deployment error and fails startup naming the setting.
/// </summary>
/// <param name="Duration">How long a reading page is kept. <c>0</c> turns page caching off.</param>
/// <param name="FeedDuration">How long the feed and the sitemap are kept. <c>0</c> turns that off.</param>
/// <param name="SizeLimitMegabytes">The memory the cache may hold.</param>
/// <param name="MaximumBodyKilobytes">The largest page that is cached; a larger one is served uncached.</param>
/// <param name="MediaMaximumBodyKilobytes">The largest image that is cached (brief 14c item 6); a larger one is streamed whole and uncached.</param>
public sealed record OutputCacheSettings(
    TimeSpan Duration,
    TimeSpan FeedDuration,
    int SizeLimitMegabytes,
    int MaximumBodyKilobytes,
    int MediaMaximumBodyKilobytes = OutputCacheSettings.DefaultMediaMaximumBodyKilobytes)
{
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan DefaultFeedDuration = TimeSpan.FromMinutes(5);
    public const int DefaultSizeLimitMegabytes = 32;
    public const int DefaultMaximumBodyKilobytes = 1024;
    public const int DefaultMediaMaximumBodyKilobytes = 512;

    /// <summary>Whether reading pages are cached at all.</summary>
    public bool PagesCached => Duration > TimeSpan.Zero;

    /// <summary>Whether the feed and the sitemap are cached.</summary>
    public bool FeedCached => FeedDuration > TimeSpan.Zero;

    /// <summary>Parses the five settings, applying the documented defaults to unset values.</summary>
    public static OutputCacheSettings Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new(
            ReadDuration(configuration, "Croft:Web:OutputCache:Duration", DefaultDuration),
            ReadDuration(configuration, "Croft:Web:OutputCache:FeedDuration", DefaultFeedDuration),
            ReadPositive(configuration, "Croft:Web:OutputCache:SizeLimitMegabytes", DefaultSizeLimitMegabytes),
            ReadPositive(configuration, "Croft:Web:OutputCache:MaximumBodyKilobytes", DefaultMaximumBodyKilobytes),
            ReadPositive(configuration, "Croft:Web:OutputCache:MediaMaximumBodyKilobytes", DefaultMediaMaximumBodyKilobytes));
    }

    private static TimeSpan ReadDuration(IConfiguration configuration, string setting, TimeSpan defaultValue)
    {
        var raw = configuration[setting];
        if (raw is null)
        {
            return defaultValue;
        }

        if (!TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var value) || value < TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{setting} must be a time span >= 0 such as 00:01:00, got '{raw}'.");
        }

        return value;
    }

    private static int ReadPositive(IConfiguration configuration, string setting, int defaultValue)
    {
        var raw = configuration[setting];
        if (raw is null)
        {
            return defaultValue;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < 1)
        {
            throw new InvalidOperationException($"{setting} must be an integer >= 1, got '{raw}'.");
        }

        return value;
    }
}
