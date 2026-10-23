using System.Globalization;
using System.Net.Http.Headers;

namespace Croft.Api.Social;

/// <summary>What the platforms' rate-limit headers say about when to ask again (brief 12a). Never earlier than now, never later than an hour.</summary>
internal static class SocialRateLimit
{
    public static readonly TimeSpan DefaultBackoff = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);

    /// <summary>The time a 429 says to wait for: <c>Retry-After</c> (seconds or a date), else Mastodon's <c>X-RateLimit-Reset</c>, else five minutes.</summary>
    public static DateTime RetryAt(HttpResponseHeaders headers, DateTime now)
    {
        var retryAfter = headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
            return Clamp(now + delta, now);

        if (retryAfter?.Date is { } date)
            return Clamp(date.UtcDateTime, now);

        return Clamp(ResetAt(headers) ?? now + DefaultBackoff, now);
    }

    /// <summary>Mastodon's <c>X-RateLimit-Reset</c> (an ISO 8601 time) or Bluesky's <c>ratelimit-reset</c> (Unix seconds), or null when absent or not a time.</summary>
    public static DateTime? ResetAt(HttpResponseHeaders headers)
    {
        if (headers.TryGetValues("X-RateLimit-Reset", out var values)
            && DateTimeOffset.TryParse(values.FirstOrDefault(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var reset))
            return reset.UtcDateTime;

        // Bluesky counts seconds since 1970; an absurd number is not a time.
        if (headers.TryGetValues("ratelimit-reset", out var unix)
            && long.TryParse(unix.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            && seconds is > 0 and < 253_402_300_799)
            return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;

        return null;
    }

    /// <summary>The time to stay away from the platform after an answer that said no rate limit is left: the reset time, or five minutes.</summary>
    public static DateTime? BusyUntil(HttpResponseHeaders headers, DateTime now)
    {
        if (!headers.TryGetValues("X-RateLimit-Remaining", out var values)
            || !int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var remaining)
            || remaining > 0)
            return null;

        return Clamp(ResetAt(headers) ?? now + DefaultBackoff, now);
    }

    private static DateTime Clamp(DateTime time, DateTime now) => time < now ? now : time > now + MaxBackoff ? now + MaxBackoff : time;
}
