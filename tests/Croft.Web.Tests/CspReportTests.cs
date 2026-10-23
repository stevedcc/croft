using System.Collections.Concurrent;
using System.Net;
using System.Text;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace Croft.Web.Tests;

/// <summary>
/// POST /csp-report: browsers report policy violations here. Bodies over 16 KB are refused
/// with 413, other content types with 415, malformed JSON with 400, and a client IP gets a
/// 429 once it exceeds the per-minute rate limit. A well-formed report is accepted with 204
/// and logged as one structured warning per violation (directive, blocked URI, document path)
/// — at most the first 25 per report plus one summary warning counting the rest, never with
/// query strings (which can carry tokens and tracking identifiers) and never with control
/// characters (which a plain-text sink would render as forged log lines).
/// </summary>
public class CspReportTests
{
    [Fact]
    public async Task Report_RejectsBodiesOver16Kilobytes()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var content = new ByteArrayContent(new byte[17 * 1024]);
        content.Headers.ContentType = new("application/csp-report");

        using var response = await client.PostAsync("/csp-report", content);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge,
            "a violation report is tiny; 17 KB is abuse and must not be read or logged");
    }

    [Fact]
    public async Task Report_LogsOneWarningPerViolation_WithoutQueryStrings()
    {
        var logs = new LogCapture();
        using var factory = CreateFactory(logSink: logs);
        using var client = factory.CreateClient();
        const string json = """
            {
              "csp-report": {
                "document-uri": "https://example.com/post/hello?utm_source=news",
                "violated-directive": "img-src",
                "blocked-uri": "https://evil.example.com/pixel.gif?session=abc",
                "status-code": 200
              }
            }
            """;

        using var response = await client.PostAsync("/csp-report", Json(json, "application/csp-report"));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent, "a received report is acknowledged and never answered with detail");
        var warnings = logs.Warnings();
        warnings.Should().HaveCount(1, "one structured warning per violation");
        warnings[0].Values.Should().Contain(new KeyValuePair<string, object?>("Directive", "img-src"));
        warnings[0].Values.Should().Contain(new KeyValuePair<string, object?>("BlockedUri", "https://evil.example.com/pixel.gif"));
        warnings[0].Values.Should().Contain(new KeyValuePair<string, object?>("DocumentPath", "/post/hello"));
    }

    [Fact]
    public async Task Report_AcceptsReportsJson_OneWarningPerViolation()
    {
        var logs = new LogCapture();
        using var factory = CreateFactory(logSink: logs);
        using var client = factory.CreateClient();
        const string json = """
            [
              {
                "type": "csp-violation",
                "body": {
                  "documentURL": "https://example.com/?x=1",
                  "violatedDirective": "style-src-elem",
                  "blockedURL": "https://evil.example.com/a.css"
                }
              },
              {
                "type": "csp-violation",
                "body": {
                  "documentURL": "https://example.com/editor",
                  "effectiveDirective": "style-src-attr",
                  "blockedURL": "inline"
                }
              }
            ]
            """;

        using var response = await client.PostAsync("/csp-report", Json(json, "application/reports+json"));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var warnings = logs.Warnings();
        warnings.Should().HaveCount(2, "the Reporting API format carries one violation per array entry");
        warnings[0].Values.Should().Contain(new KeyValuePair<string, object?>("Directive", "style-src-elem"));
        warnings[0].Values.Should().Contain(new KeyValuePair<string, object?>("DocumentPath", "/"));
        warnings[1].Values.Should().Contain(new KeyValuePair<string, object?>("Directive", "style-src-attr"));
        warnings[1].Values.Should().Contain(new KeyValuePair<string, object?>("BlockedUri", "inline"));
    }

    [Fact]
    public async Task Report_AcceptsWebKitsEnvelope_ALoneReportingApiObjectSentAsApplicationCspReport()
    {
        // Measured with Playwright's WebKit 26.6 (brief 10c): the document names a report-to
        // group AND a report-uri, and WebKit posts to report-uri with the legacy media type
        // application/csp-report but a Reporting-API-shaped body: one object, not an array, and no
        // "csp-report" wrapper. Such a report used to be accepted (204) and silently dropped.
        var logs = new LogCapture();
        using var factory = CreateFactory(logSink: logs);
        using var client = factory.CreateClient();
        const string json = """
            {
              "type": "csp-violation",
              "url": "https://example.com/post/hello",
              "body": {
                "documentURL": "https://example.com/post/hello?x=1",
                "disposition": "enforce",
                "effectiveDirective": "script-src-elem",
                "blockedURL": "https://example.com/_content/MudBlazor/MudBlazor.min.js"
              }
            }
            """;

        using var response = await client.PostAsync("/csp-report", Json(json, "application/csp-report"));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var warnings = logs.Warnings();
        warnings.Should().HaveCount(1, "the report is one violation, whatever envelope the browser chose");
        warnings[0].Values.Should().Contain(new KeyValuePair<string, object?>("Directive", "script-src-elem"));
        warnings[0].Values.Should().Contain(new KeyValuePair<string, object?>("BlockedUri", "https://example.com/_content/MudBlazor/MudBlazor.min.js"));
        warnings[0].Values.Should().Contain(new KeyValuePair<string, object?>("DocumentPath", "/post/hello"));
    }

    [Fact]
    public async Task Report_LogsAtMost25ViolationsPerReport_AndSummarizesTheRest()
    {
        var logs = new LogCapture();
        using var factory = CreateFactory(logSink: logs);
        using var client = factory.CreateClient();
        var entries = string.Join(
            ",",
            Enumerable.Range(0, 30).Select(index =>
                $$$"""
                {"type":"csp-violation","body":{"violatedDirective":"img-src","blockedURL":"https://evil.example.com/p{{{index}}}.gif"}}
                """));

        using var response = await client.PostAsync("/csp-report", Json($"[{entries}]", "application/reports+json"));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent, "the cap changes what is logged, never the 204 semantics");
        var warnings = logs.Warnings();
        warnings.Should().HaveCount(26, "a 30-entry report logs 25 violation lines plus one summary line for the rest");
        warnings.Take(25).Should().OnlyContain(
            warning => warning.Values.Any(pair => pair.Key == "Directive"),
            "the first 25 violations are logged individually");
        warnings[25].Values.Should().Contain(new KeyValuePair<string, object?>("Suppressed", 5),
            "the summary line counts the violations the report carried beyond the 25 logged");
    }

    [Fact]
    public async Task Report_StripsControlCharacters_NoReportFieldCanForgeALogLine()
    {
        var logs = new LogCapture();
        using var factory = CreateFactory(logSink: logs);
        using var client = factory.CreateClient();
        const string json = """
            {
              "csp-report": {
                "document-uri": "https://example.com/post/hello\r\nFAKE DOC",
                "violated-directive": "img-src\r\nFAKE DIRECTIVE",
                "blocked-uri": "https://evil.example.com/pixel.gif\r\nFAKE LINE"
              }
            }
            """;

        using var response = await client.PostAsync("/csp-report", Json(json, "application/csp-report"));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var entry = logs.Warnings().Single();
        foreach (var (key, value) in entry.Values.Where(pair => pair.Value is string))
        {
            ((string)value!).Any(char.IsControl).Should().BeFalse(
                $"{key} is logged to a plain-text sink and must not carry line breaks or other control characters");
        }

        entry.Message.Should().NotContain("\r").And.NotContain("\n",
            "the report's CRLFs must not split the warning into several lines");
        entry.Message.Split('\n').Should().OnlyContain(
            line => !line.StartsWith("FAKE"),
            "a report must not be able to forge a log line that starts with its own text");
    }

    [Fact]
    public async Task Report_RateLimitsPerClientIp()
    {
        using var factory = CreateFactory(settings: new() { ["Csp:ReportRateLimitPerMinute"] = "2" });
        using var client = factory.CreateClient();

        (await client.PostAsync("/csp-report", Json("""{"csp-report":{"violated-directive":"img-src"}}""", "application/csp-report")))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.PostAsync("/csp-report", Json("""{"csp-report":{"violated-directive":"img-src"}}""", "application/csp-report")))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.PostAsync("/csp-report", Json("""{"csp-report":{"violated-directive":"img-src"}}""", "application/csp-report")))
            .StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
                "a client flooding the endpoint with reports is throttled in-memory per IP");
    }

    [Fact]
    public async Task Report_RejectsOtherContentTypes()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/csp-report",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType,
            "only the two report formats are accepted");
    }

    [Fact]
    public async Task Report_AnswersMalformedJsonWith400()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/csp-report",
            new StringContent("{not json", Encoding.UTF8, "application/csp-report"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static StringContent Json(string json, string mediaType) =>
        new(json, Encoding.UTF8, mediaType);

    private static WebApplicationFactory<Program> CreateFactory(
        Dictionary<string, string?>? settings = null,
        LogCapture? logSink = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("Blog:BaseUrl", "https://example.com/");
            // Outside Development the app refuses to start without the analytics ingest key.
            builder.UseSetting("Analytics:IngestKey", "test-ingest-key");
            builder.UseEnvironment("Production");
            if (settings is not null)
            {
                foreach (var (key, value) in settings)
                {
                    builder.UseSetting(key, value);
                }
            }

            if (logSink is not null)
            {
                builder.ConfigureLogging(logging => logging.AddProvider(logSink));
            }
        });

    /// <summary>Captures the structured values of every log entry so tests can assert on them.</summary>
    private sealed class LogCapture : ILoggerProvider
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public List<LogEntry> Warnings() =>
            [.. Entries.Where(entry => entry.Level == LogLevel.Warning)];

        public ILogger CreateLogger(string categoryName) => new Capture(Entries);

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }

        private sealed class Capture(ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(new LogEntry(
                    logLevel,
                    state is IEnumerable<KeyValuePair<string, object?>> values ? [.. values] : [],
                    formatter(state, exception)));
        }
    }

    private sealed record LogEntry(LogLevel Level, List<KeyValuePair<string, object?>> Values, string Message);
}
