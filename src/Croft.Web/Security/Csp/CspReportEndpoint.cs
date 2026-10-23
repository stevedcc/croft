using System.Buffers;
using System.Text.Json;
using Croft.Shared;
using Croft.Web.Telemetry;

namespace Croft.Web.Security.Csp;

/// <summary>
/// POST /csp-report: where the policy's report-uri and report-to point browsers at. Accepts
/// the legacy application/csp-report body and the Reporting API's application/reports+json
/// one, refuses bodies over 16 KB with 413 and a flooding client with 429, and logs one
/// structured warning per violation — at most the first 25 of a report, with one summary
/// warning counting the rest, because the fields are attacker-controlled and a 16 KB body
/// could otherwise carry hundreds of violations into the log. Directive, blocked URI and
/// document path are logged with query strings stripped (they can carry tokens and tracking
/// identifiers) and control characters removed: a plain-text console sink renders those
/// verbatim, and a newline inside a value would forge a log line. Every accepted report is
/// answered 204: reports are for the operator, never for the browser.
/// </summary>
public sealed partial class CspReportEndpoint(
    ILogger<CspReportEndpoint> logger, CspReportRateLimiter limiter, ArrayPool<byte> bufferPool)
{
    private const int MaxBodyBytes = 16 * 1024;
    private const int MaxLoggedViolations = 25;

    public async Task<IResult> HandleAsync(HttpContext context, CancellationToken ct)
    {
        var mediaType = context.Request.ContentType?.Split(';')[0].Trim();
        var isLegacy = string.Equals(mediaType, "application/csp-report", StringComparison.OrdinalIgnoreCase);
        var isReportsApi = string.Equals(mediaType, "application/reports+json", StringComparison.OrdinalIgnoreCase);
        if (!isLegacy && !isReportsApi)
        {
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }

        if (context.Request.ContentLength > MaxBodyBytes)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        // The limiter runs before the body buffer is rented: a refused report costs no allocation.
        // The key is the client's IPv4 address or IPv6 /64 (RequestLimits.ClientKey), so rotating
        // addresses inside a /64 does not buy a fresh budget.
        if (!limiter.TryAccept(RequestLimits.ClientKey(context.Connection.RemoteIpAddress) ?? "unknown"))
        {
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        // The parsed document points into the buffer (JsonDocument.Parse copies nothing), so the
        // buffer goes back to the pool only after the document has been used and disposed.
        var body = bufferPool.Rent(MaxBodyBytes + 1);
        try
        {
            var total = 0;
            int read;
            while ((read = await context.Request.Body.ReadAsync(body.AsMemory(total, MaxBodyBytes + 1 - total), ct)) > 0)
            {
                total += read;
                if (total > MaxBodyBytes)
                {
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(body.AsMemory(0, total));
            }
            catch (JsonException)
            {
                return Results.BadRequest();
            }

            using (document)
            {
                LogViolations(document.RootElement);
            }

            return Results.NoContent();
        }
        finally
        {
            bufferPool.Return(body);
        }
    }

    private void LogViolations(JsonElement root)
    {
        var logged = 0;
        var suppressed = 0;
        foreach (var violation in ParseViolations(root))
        {
            // Every reported violation counts, even beyond the logging cap below: the
            // effective directive is the only attribute — the blocked URI and the document
            // path can carry reader data and are never metric attributes. The endpoint is
            // anonymous, so the directive is reduced to the policy's fixed vocabulary: a
            // stranger must not be able to multiply the collector's series with made-up
            // directive names.
            WebMetrics.CspViolationReported(EffectiveDirectiveName(violation.Directive));

            if (logged == MaxLoggedViolations)
            {
                suppressed++;
                continue;
            }

            LogViolation(logger, violation.Directive, violation.BlockedUri, violation.DocumentPath);
            logged++;
        }

        if (suppressed > 0)
        {
            LogSuppressedViolations(logger, suppressed);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "CSP violation: {Directive} blocked {BlockedUri} on {DocumentPath}")]
    private static partial void LogViolation(ILogger logger, string directive, string blockedUri, string documentPath);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "CSP report carried {Suppressed} more violations; logging is capped per report")]
    private static partial void LogSuppressedViolations(ILogger logger, int suppressed);

    /// <summary>The metric's directive attribute: the directive's name when the policy knows it,
    /// otherwise one shared "other" series — reports are attacker-controlled and the number of
    /// series must stay small. The logs keep the value exactly as reported.</summary>
    private static string EffectiveDirectiveName(string directive)
    {
        var name = directive.Split(' ', 2)[0];
        return CspPolicy.IsKnownDirective(name) ? name : "other";
    }

    /// <summary>
    /// The violations in a report body. The shape decides, not the media type: WebKit posts a
    /// lone Reporting-API object ({"type":"csp-violation","body":{…}}) under the legacy
    /// application/csp-report type, Chromium and Firefox post an array under reports+json, and the
    /// legacy format wraps one report in {"csp-report":{…}}. A body that is none of these yields
    /// nothing (it is still acknowledged: reports are fire-and-forget).
    /// </summary>
    private static IEnumerable<CspViolation> ParseViolations(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("csp-report", out var report))
            {
                if (ViolationFrom(report) is { } legacy)
                {
                    yield return legacy;
                }
            }
            else if (ReportingApiViolation(root) is { } single)
            {
                yield return single;
            }

            yield break;
        }

        if (root.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var entry in root.EnumerateArray())
        {
            if (ReportingApiViolation(entry) is { } violation)
            {
                yield return violation;
            }
        }
    }

    private static CspViolation? ReportingApiViolation(JsonElement entry) =>
        entry.ValueKind == JsonValueKind.Object &&
        (!entry.TryGetProperty("type", out var type) || type.GetString() == "csp-violation") &&
        entry.TryGetProperty("body", out var body)
            ? ViolationFrom(body)
            : null;

    private static CspViolation? ViolationFrom(JsonElement report)
    {
        var directive = FirstString(report, "violated-directive", "violatedDirective", "effective-directive", "effectiveDirective");
        var blockedUri = FirstString(report, "blocked-uri", "blockedURL", "blockedUri");
        var documentUri = FirstString(report, "document-uri", "documentURL", "documentUri");
        if (directive is null && blockedUri is null && documentUri is null)
        {
            return null;
        }

        return new CspViolation(
            Sanitize(directive ?? string.Empty),
            Sanitize(blockedUri is null ? string.Empty : StripQuery(blockedUri)),
            Sanitize(documentUri is null ? string.Empty : DocumentPath(documentUri)));
    }

    /// <summary>Every logged field is attacker-controlled; a plain-text console sink renders
    /// control characters verbatim, so a newline inside a value would forge a log line. The
    /// value is reduced to its non-control characters — one line, nothing escaped.</summary>
    private static string Sanitize(string value) =>
        value.Any(char.IsControl) ? string.Concat(value.Where(static c => !char.IsControl(c))) : value;

    private static string? FirstString(JsonElement element, params ReadOnlySpan<string> names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static string DocumentPath(string documentUri) =>
        Uri.TryCreate(documentUri, UriKind.Absolute, out var uri)
            ? uri.AbsolutePath
            : StripQuery(documentUri);

    private static string StripQuery(string value)
    {
        var cut = value.IndexOfAny(['?', '#']);
        return cut < 0 ? value : value[..cut];
    }

    private sealed record CspViolation(string Directive, string BlockedUri, string DocumentPath);
}
