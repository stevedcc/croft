using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Croft.Api.Social;

/// <summary>
/// How an announcer reads a platform's answer to a request (brief 12d). The reasons that come out are what the database stores and the editor shows:
/// the network, a status code and (for an error that cannot heal) the platform's own <c>error</c> string, capped, with the credential cut out if the
/// platform echoed it. Never a response body beyond that string.
/// </summary>
internal static partial class AnnounceErrors
{
    private const int MaxMessageLength = 200;

    /// <summary>Errors a retry cannot fix: the request is wrong or the credentials are (400, 401, 403, 404, 422).</summary>
    public static bool IsPermanent(HttpStatusCode status) =>
        status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity;

    /// <summary>
    /// Retry or Failed for an answer that was not a success. <paramref name="what"/> completes "refused the ..." ("post", "account lookup"). 429 retries at the time
    /// the answer names (<paramref name="retryAt"/>); every other status that is not permanent (408, 5xx, anything unexpected) retries without a time.
    /// </summary>
    public static AnnounceOutcome Classify(string network, string what, HttpStatusCode status, byte[] body, Func<DateTime> retryAt, params string[] secrets)
    {
        if (status == HttpStatusCode.TooManyRequests)
            return new AnnounceOutcome.Retry($"{network} is rate limiting (HTTP 429)", retryAt());

        if (!IsPermanent(status))
            return new AnnounceOutcome.Retry($"{network} answered HTTP {(int)status}", null);

        var reason = $"{network} refused the {what} (HTTP {(int)status})";
        return new AnnounceOutcome.Failed(ErrorMessage(body, secrets) is { Length: > 0 } message ? $"{reason}: {message}" : reason);
    }

    /// <summary>For the outcome log line: " (HTTP 503)" when the outcome's reason names a status (the reasons <see cref="Classify"/> writes all do), else empty.
    /// Only the number is taken from the reason; the reason itself is never logged.</summary>
    public static string HttpStatusOf(AnnounceOutcome outcome)
    {
        var reason = outcome switch
        {
            AnnounceOutcome.Retry retry => retry.Reason,
            AnnounceOutcome.Failed failed => failed.Reason,
            AnnounceOutcome.Posted or AnnounceOutcome.NotFound or null => string.Empty,
        };
        return StatusInReason().Match(reason) is { Success: true } match ? $" (HTTP {match.Groups["status"].Value})" : string.Empty;
    }

    [GeneratedRegex(@"HTTP (?<status>[1-5][0-9]{2})\b", RegexOptions.CultureInvariant)]
    private static partial Regex StatusInReason();

    /// <summary>The platform's <c>error</c> string when the body is JSON with one: single line, capped, the secrets cut out. Empty otherwise.</summary>
    public static string ErrorMessage(byte[] body, params string[] secrets)
    {
        string? message;
        try
        {
            using var document = SocialJson.Parse(Encoding.UTF8.GetString(body), "error");
            message = document.RootElement.Str("error");
        }
        catch (SocialParseException)
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(message))
            return string.Empty;

        foreach (var secret in secrets.Where(secret => !string.IsNullOrEmpty(secret)))
            message = message.Replace(secret, "[removed]", StringComparison.Ordinal);

        var line = string.Concat(message.Select(c => char.IsControl(c) ? ' ' : c)).Trim();
        return line.Length > MaxMessageLength ? line[..MaxMessageLength] + "…" : line;
    }

    /// <summary>The expected failures of sending (see <see cref="SocialFetchErrors"/>) become a retry; anything else is a bug and propagates.</summary>
    public static bool IsExpected(Exception exception, CancellationToken ct) => SocialFetchErrors.IsExpected(exception, ct);
}
