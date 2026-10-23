using System.Diagnostics;
using System.Net;
using Croft.Web.Tests.Fixtures;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Croft.Web.Tests;

/// <summary>
/// The Web app's telemetry setup (Croft.Shared.CroftTelemetry): with OTEL_EXPORTER_OTLP_ENDPOINT
/// set the app exports spans, metrics and logs through the standard OTLP pipeline; without it
/// nothing is registered and the app behaves exactly as before. Health probes are never traced and
/// no exported attribute carries reader data (the site's privacy statement). Requests carry a
/// known traceparent, so assertions scope to this request's trace — other test classes run in
/// parallel in this process and their spans land in the same capture.
/// </summary>
public class WebTelemetryTests
{
    [Fact]
    public async Task WithoutOtlpEndpoint_NoTelemetryIsRegistered_AndRequestsStillSucceed()
    {
        using var factory = CreateFactory(capture: null, otlpEndpoint: "");

        factory.Services.GetService<TracerProvider>().Should().BeNull(
            "without OTEL_EXPORTER_OTLP_ENDPOINT no tracer provider may exist");
        factory.Services.GetService<MeterProvider>().Should().BeNull(
            "without OTEL_EXPORTER_OTLP_ENDPOINT no meter provider may exist");
        factory.Services.GetServices<ILoggerProvider>().Should().NotContain(p => p is OpenTelemetryLoggerProvider,
            "the OpenTelemetry logging provider must not be attached to the console-only pipeline");

        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "requests must succeed exactly as they did before telemetry existed");
    }

    [Fact]
    public async Task WithOtlpEndpoint_RequestProducesServerSpan()
    {
        var capture = new TelemetryCapture();
        using var factory = CreateFactory(capture);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/", out var traceId);
        response.EnsureSuccessStatusCode();

        var span = await capture.WaitForSpanAsync(
            s => s.TraceId == traceId && s.Kind == ActivityKind.Server,
            "the server span for the request");
        span.TraceId.Should().Be(traceId, "the request's trace id must carry onto the span");
    }

    [Fact]
    public async Task HealthRequests_ProduceNoSpans()
    {
        var capture = new TelemetryCapture();
        using var factory = CreateFactory(capture);
        using var client = factory.CreateClient();

        using var liveResponse = await client.GetAsync("/health/live", out var liveTrace);
        liveResponse.EnsureSuccessStatusCode();
        using var readyResponse = await client.GetAsync("/health/ready", out var readyTrace);
        readyResponse.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable);

        // Positive control: an ordinary request in the same capture must show up, so the empty
        // result below means "the probes were not traced", not "nothing is traced".
        using var response = await client.GetAsync("/", out var traceId);
        response.EnsureSuccessStatusCode();
        await capture.WaitForSpanAsync(s => s.TraceId == traceId && s.Kind == ActivityKind.Server,
            "the control request's span");

        capture.Spans.Where(s => s.TraceId == liveTrace || s.TraceId == readyTrace).Should().BeEmpty(
            "kubelet probes every few seconds; tracing them would drown every real request");
    }

    [Fact]
    public async Task HttpRequestDurationMetric_IsRecordedWithRouteAndStatusCode()
    {
        var capture = new TelemetryCapture();
        using var factory = CreateFactory(capture);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/");
        response.EnsureSuccessStatusCode();
        // The duration is recorded when the hosting layer disposes the request, after the response
        // has reached the client: wait for the metric to arrive in the capture instead of collecting
        // once and hoping it is there.
        await capture.WaitForMetricAsync(factory.Services,
            m => m.Name == "http.server.request.duration",
            "the request's http.server.request.duration");

        // Exports accumulate one Metric snapshot per collection; assert the recorded points
        // across all of them rather than assuming a single export.
        var points = new List<List<KeyValuePair<string, object?>>>();
        foreach (var metric in capture.Metrics.Where(m => m.Name == "http.server.request.duration"))
        {
            foreach (var point in metric.GetMetricPoints())
            {
                var tags = new List<KeyValuePair<string, object?>>();
                foreach (var tag in point.Tags)
                {
                    tags.Add(tag);
                }

                points.Add(tags);
            }
        }

        points.Should().NotBeEmpty("the request must record into http.server.request.duration");
        points.Should().Contain(tags =>
                tags.Any(t => t.Key == "http.route") &&
                tags.Any(t => t.Key == "http.response.status_code" && Convert.ToString(t.Value) == "200"),
            "request rates and latency per route and status are what the owner's dashboards read");
    }

    [Fact]
    public async Task CspViolationLog_IsExportedWithNamedFieldsAndTheRequestTraceId()
    {
        var capture = new TelemetryCapture();
        using var factory = CreateFactory(capture);
        using var client = factory.CreateClient();

        const string json = """
            {
              "csp-report": {
                "document-uri": "https://example.com/post/hello",
                "violated-directive": "img-src",
                "blocked-uri": "https://evil.example.com/pixel.gif",
                "status-code": 200
              }
            }
            """;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/csp-report")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/csp-report"),
        };
        var traceId = AttachTrace(request);
        using var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var log = await WaitForLogAsync(capture, record => record.TraceId == traceId,
            "the CSP violation log of this request");
        log.CategoryName.Should().Contain("CspReportEndpoint", "the [LoggerMessage] the endpoint writes is the log under test");
        log.Attributes.Should().Contain(t => t.Key == "Directive" && Equals(t.Value, "img-src"),
            "named fields must arrive as attributes, as a collector receives them");
        log.Attributes.Should().Contain(t => t.Key == "BlockedUri" && Equals(t.Value, "https://evil.example.com/pixel.gif"));
        log.Attributes.Should().Contain(t => t.Key == "DocumentPath" && Equals(t.Value, "/post/hello"));
        log.FormattedMessage.Should().Contain("img-src", "the formatted message must be exported too");
    }

    [Fact]
    public async Task Privacy_NoClientIpCookieTokenEmailOrQueryString_InAnyExportedAttribute()
    {
        var capture = new TelemetryCapture();
        using var factory = CreateFactory(capture, remoteIp: "203.0.113.7");
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/?q=secretterm&email=reader@example.com");
        request.Headers.TryAddWithoutValidation("Cookie", "blog_sid=TOPSECRET-COOKIE-VALUE");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer TOPSECRET-TOKEN-VALUE");
        var traceId = AttachTrace(request);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        // The request-duration metric is recorded after the response is sent: without waiting for it the
        // search below could run over a capture that holds no metric points at all and pass vacuously.
        await capture.WaitForMetricAsync(factory.Services,
            m => m.Name == "http.server.request.duration",
            "the request's http.server.request.duration, so its tags are among the attributes searched");

        (await capture.WaitForSpanAsync(s => s.TraceId == traceId && s.Kind == ActivityKind.Server,
                "the request must be traced or this test proves nothing"))
            .Should().NotBeNull();
        // The export of the request's logs is asynchronous; searching an empty capture would
        // prove nothing.
        await capture.WaitForLogAsync(record => record.TraceId == traceId,
            "the request's log records");
        capture.AllAttributeValues().Should().NotContain(value =>
                value.Contains("secretterm") ||
                value.Contains("reader@example.com") ||
                value.Contains("TOPSECRET-COOKIE-VALUE") ||
                value.Contains("TOPSECRET-TOKEN-VALUE") ||
                value.Contains("203.0.113.7"),
            "the site's privacy statement promises none of these reach the collector");
        capture.AllAttributeValues().Should().NotContain(
                value => System.Text.RegularExpressions.Regex.IsMatch(value, @"\?\w+="),
            "no exported attribute may carry a query string at all: search terms are reader input, and "
            + "the framework's own value redaction still leaves the keys visible");
    }

    internal static WebApplicationFactory<Program> CreateFactory(
        TelemetryCapture? capture,
        string otlpEndpoint = TelemetryCapture.EnabledEndpoint,
        string? remoteIp = null,
        bool signedIn = false)
    {
        var api = new StubBlogApiHandler();
        // The telemetry setup reads OTEL_* while the host is being configured, so the tests set
        // them through UseSetting (early, like any environment variable) rather than the late
        // in-memory configuration — that one is not visible yet at setup time.
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.test/");
            builder.UseSetting("Blog:BaseUrl", "https://blog.test/");
            builder.UseSetting("OTEL_EXPORTER_OTLP_ENDPOINT", otlpEndpoint);
            builder.ConfigureServices(services =>
            {
                capture?.Register(services);
                services.AddHttpClient("BlogApi").ConfigurePrimaryHttpMessageHandler(() => api);
                if (signedIn)
                {
                    SignedInAdmin.AddTo(services);
                }
                if (remoteIp is not null)
                {
                    services.AddSingleton<IStartupFilter>(new RemoteIpStartupFilter(IPAddress.Parse(remoteIp)));
                }
            });
        });
    }

    internal static ActivityTraceId AttachTrace(HttpRequestMessage request)
    {
        var traceId = ActivityTraceId.CreateRandom();
        request.Headers.TryAddWithoutValidation("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01");
        return traceId;
    }

    private static async Task<OpenTelemetry.Logs.LogRecord> WaitForLogAsync(
        TelemetryCapture capture, Func<OpenTelemetry.Logs.LogRecord, bool> predicate, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var log in capture.Logs)
            {
                if (predicate(log))
                {
                    return log;
                }
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"no exported log record matched: {because}");
    }
}

internal static class WebTelemetryTestExtensions
{
    /// <summary>Sends the request carrying a known W3C traceparent and returns its trace id.</summary>
    public static Task<HttpResponseMessage> GetAsync(this HttpClient client, string path, out ActivityTraceId traceId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        traceId = ActivityTraceId.CreateRandom();
        request.Headers.TryAddWithoutValidation("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01");
        return client.SendAsync(request);
    }
}

/// <summary>
/// Gives every test request a known client address, so the privacy test can prove the address is
/// not exported instead of passing on a null value.
/// </summary>
internal sealed class RemoteIpStartupFilter(IPAddress address) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            context.Connection.RemoteIpAddress = address;
            return nextMiddleware();
        });
        next(app);
    };
}
