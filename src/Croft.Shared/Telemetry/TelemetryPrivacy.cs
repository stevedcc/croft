using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace Croft.Shared.Telemetry;

/// <summary>
/// The privacy rule the site's privacy statement promises: no exported span, metric or log
/// attribute carries a client IP address, a token, a cookie value, an email address or a
/// query-string value (search terms are reader input). Attributes with credential- or
/// identity-shaped keys are dropped; URL-shaped values keep their path and lose the query string.
/// </summary>
internal static partial class TelemetryPrivacy
{
    /// <summary>Keys that must never be exported at all.</summary>
    private static readonly string[] ForbiddenKeyParts =
    [
        "cookie",
        "authorization",
        "token",
        "password",
        "secret",
        "email",
        "client.address",
        "client.port",
        "x-forwarded",
        "x-visitor-ip",
    ];

    /// <summary>Keys whose values are URLs or paths: reader input hides in their query strings.</summary>
    private static readonly string[] QueryBearingKeyParts =
    [
        "url",
        "uri",
        "target",
        "path",
        "document",
        "blocked",
        "referrer",
        "referer",
        "location",
    ];

    public static bool IsForbiddenKey(string key)
        => ForbiddenKeyParts.Any(part => key.Contains(part, StringComparison.OrdinalIgnoreCase));

    public static bool IsQueryBearingKey(string key)
        => QueryBearingKeyParts.Any(part => key.Contains(part, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a free-form value carries a query string (a "?key=" run).</summary>
    public static bool CarriesQueryString(string value)
        => QueryRun().IsMatch(value);

    [GeneratedRegex(@"\?\w+=")]
    private static partial Regex QueryRun();

    /// <summary>Removes the query string (and fragment) from a URL-shaped value, keeping the path.</summary>
    public static string StripQuery(string value)
    {
        var cut = value.IndexOfAny(['?', '#']);
        return cut < 0 ? value : value[..cut];
    }

    /// <summary>
    /// Removes query strings from free text (log bodies), leaving prose alone: only a "?..." run
    /// that looks like a query string (carries a key=value pair) is cut.
    /// </summary>
    public static string ScrubText(string text)
    {
        if (text.IndexOf('?') < 0)
        {
            return text;
        }

        var result = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '?')
            {
                var end = i + 1;
                while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] is not '"' and not '\'')
                {
                    end++;
                }

                if (text.AsSpan(i + 1, end - i - 1).IndexOf('=') >= 0)
                {
                    i = end;
                    continue;
                }
            }

            result.Append(text[i]);
            i++;
        }

        return result.ToString();
    }
}

internal sealed class ActivityPrivacyScrubber : BaseProcessor<Activity>
{
    public override void OnEnd(Activity data)
    {
        // Two passes: SetTag changes the collection being enumerated, so the first pass only notes
        // what must change (nothing, for almost every span: no allocation) and the second applies it.
        List<(string Key, string? Value)>? edits = null;
        foreach (var (key, value) in data.TagObjects)
        {
            if (TelemetryPrivacy.IsForbiddenKey(key))
            {
                (edits ??= []).Add((key, null));
            }
            else if (value is string text && (TelemetryPrivacy.IsQueryBearingKey(key) || TelemetryPrivacy.CarriesQueryString(text)))
            {
                var stripped = TelemetryPrivacy.StripQuery(text);
                if (!ReferenceEquals(stripped, text))
                {
                    (edits ??= []).Add((key, stripped));
                }
            }
        }

        if (edits is null)
        {
            return;
        }

        foreach (var (key, value) in edits)
        {
            data.SetTag(key, value);
        }
    }
}

internal sealed class LogRecordPrivacyScrubber : BaseProcessor<LogRecord>
{
    public override void OnEnd(LogRecord data)
    {
        if (data.Attributes is { } attributes)
        {
            List<KeyValuePair<string, object?>> scrubbed = [with(capacity: attributes.Count)];
            foreach (var (key, value) in attributes)
            {
                if (TelemetryPrivacy.IsForbiddenKey(key))
                {
                    continue;
                }

                scrubbed.Add(new(key,
                    value is string text && (TelemetryPrivacy.IsQueryBearingKey(key) || TelemetryPrivacy.CarriesQueryString(text))
                        ? TelemetryPrivacy.StripQuery(text)
                        : value));
            }

            data.Attributes = scrubbed;
        }

        if (data.FormattedMessage is { } formatted)
        {
            data.FormattedMessage = TelemetryPrivacy.ScrubText(formatted);
        }

        if (data.Body is { } body)
        {
            data.Body = TelemetryPrivacy.ScrubText(body);
        }
    }
}
