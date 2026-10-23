using System.Diagnostics;
using System.Net;
using Croft.Api.Tests.Fixtures;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Croft.Api.Tests;

/// <summary>
/// The API's telemetry setup (Croft.Shared.CroftTelemetry): with OTEL_EXPORTER_OTLP_ENDPOINT set
/// the app exports spans, metrics and logs through the standard OTLP pipeline; without it nothing
/// is registered and the app behaves exactly as before. Health probes are never traced and no
/// exported attribute carries reader data (the site's privacy statement). Every request carries a
/// known traceparent, so assertions scope to this request's trace — xUnit runs test classes in
/// parallel in this process and their spans land in the same capture.
/// </summary>
public class ApiTelemetryTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    [Fact]
    public async Task WithoutOtlpEndpoint_NoTelemetryIsRegistered_AndRequestsStillSucceed()
    {
        using var factory = CreateFactory(configureServices: null, otlpEndpoint: "");

        factory.Services.GetService<TracerProvider>().Should().BeNull(
            "without OTEL_EXPORTER_OTLP_ENDPOINT no tracer provider may exist");
        factory.Services.GetService<MeterProvider>().Should().BeNull(
            "without OTEL_EXPORTER_OTLP_ENDPOINT no meter provider may exist");
        factory.Services.GetServices<ILoggerProvider>().Should().NotContain(p => p is OpenTelemetryLoggerProvider,
            "the OpenTelemetry logging provider must not be attached to the console-only pipeline");

        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/posts");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "requests must succeed exactly as they did before telemetry existed");
    }

    [Fact]
    public async Task WithOtlpEndpoint_RequestProducesServerSpan()
    {
        var capture = new TelemetryCapture();
        using var factory = CreateFactory(capture.Register, TelemetryCapture.EnabledEndpoint);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/posts", out var traceId);
        response.EnsureSuccessStatusCode();

        var span = await capture.WaitForSpanAsync(
            s => s.TraceId == traceId && s.Kind == ActivityKind.Server && PathOf(s).StartsWith("/api/posts", StringComparison.Ordinal),
            "the server span for the request");
        span.TraceId.Should().Be(traceId, "the request's trace id must carry onto the span");
    }

    [Fact]
    public async Task HealthRequest_ProducesNoSpans()
    {
        var capture = new TelemetryCapture();
        using var factory = CreateFactory(capture.Register, TelemetryCapture.EnabledEndpoint);
        using var client = factory.CreateClient();

        using var healthResponse = await client.GetAsync("/health", out var healthTrace);
        healthResponse.EnsureSuccessStatusCode();

        // Positive control: an ordinary request in the same capture must show up, so the empty
        // result below means "the health request was not traced", not "nothing is traced".
        using var response = await client.GetAsync("/api/posts", out var traceId);
        response.EnsureSuccessStatusCode();
        await capture.WaitForSpanAsync(
            s => s.TraceId == traceId && s.Kind == ActivityKind.Server,
            "the control request's span");

        capture.Spans.Where(s => s.TraceId == healthTrace).Should().BeEmpty(
            "kubelet probes every few seconds; tracing them would drown every real request");
    }

    [Fact]
    public async Task HttpRequestDurationMetric_IsRecordedWithRouteAndStatusCode()
    {
        var capture = new TelemetryCapture();
        using var factory = CreateFactory(capture.Register, TelemetryCapture.EnabledEndpoint);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/posts");
        response.EnsureSuccessStatusCode();
        TelemetryCapture.CollectMetrics(factory.Services);

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
                tags.Any(t => t.Key == "http.route" && Convert.ToString(t.Value)!.Contains("api/posts")) &&
                tags.Any(t => t.Key == "http.response.status_code" && Convert.ToString(t.Value) == "200"),
            "request rates and latency per route and status are what the owner's dashboards read");
    }

    [Fact]
    public async Task ApiRequest_QueriesDatabase_ProducesNpgsqlSpanUnderTheServerSpan()
    {
        var capture = new TelemetryCapture();
        using var factory = CreateFactory(capture.Register, TelemetryCapture.EnabledEndpoint);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/posts", out var traceId);
        response.EnsureSuccessStatusCode();

        var server = await capture.WaitForSpanAsync(
            s => s.TraceId == traceId && s.Kind == ActivityKind.Server && PathOf(s).StartsWith("/api/posts", StringComparison.Ordinal),
            "the server span for the request");
        var npgsql = capture.Spans.Where(s => s.TraceId == traceId && s.Source.Name == "Npgsql").ToList();
        npgsql.Should().NotBeEmpty("the request reads the database, and that must show up as Npgsql spans");
        npgsql.Should().OnlyContain(s => s.ParentSpanId == server.SpanId,
            "every query belongs to the request that ran it");
    }

    [Fact]
    public async Task Privacy_NoClientIpCookieTokenEmailOrQueryString_InAnyExportedAttribute()
    {
        var capture = new TelemetryCapture();
        using var factory = CreateFactory(capture.Register, TelemetryCapture.EnabledEndpoint, remoteIp: "203.0.113.7");
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/posts?q=secretterm&email=reader@example.com");
        request.Headers.TryAddWithoutValidation("Cookie", "blog_sid=TOPSECRET-COOKIE-VALUE");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer TOPSECRET-TOKEN-VALUE");
        var traceId = AttachTrace(request);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        TelemetryCapture.CollectMetrics(factory.Services);

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

    [Fact]
    public async Task Resource_CarriesServiceNameVersionAndExtraAttributes()
    {
        using var factory = CreateFactory(configureServices: null, TelemetryCapture.EnabledEndpoint);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/posts");
        response.EnsureSuccessStatusCode();

        var attributes = factory.Services.GetRequiredService<TracerProvider>().GetResource().Attributes.ToList();
        attributes.Should().Contain(t => t.Key == "service.name" && Convert.ToString(t.Value) == "croft-api",
            "the standard service.name defaults to the app's name");
        attributes.Should().Contain(t => t.Key == "service.version" && (Convert.ToString(t.Value) ?? "").Length > 0,
            "the assembly's informational version identifies the running build");
    }

    [Fact]
    public async Task Resource_ServiceNameIsOverridable_AndResourceAttributesAreAdded()
    {
        using var factory = CreateFactory(
            configureServices: null,
            TelemetryCapture.EnabledEndpoint,
            extraConfig: new Dictionary<string, string?>
            {
                ["OTEL_SERVICE_NAME"] = "blog-api-blue",
                ["OTEL_RESOURCE_ATTRIBUTES"] = "deployment.environment=prod,k8s.namespace.name=blogs",
            });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/posts");
        response.EnsureSuccessStatusCode();

        var attributes = factory.Services.GetRequiredService<TracerProvider>().GetResource().Attributes.ToList();
        attributes.Should().Contain(t => t.Key == "service.name" && Convert.ToString(t.Value) == "blog-api-blue",
            "OTEL_SERVICE_NAME is the standard override for service.name");
        attributes.Should().Contain(t => t.Key == "deployment.environment" && Convert.ToString(t.Value) == "prod",
            "OTEL_RESOURCE_ATTRIBUTES adds resource attributes");
        attributes.Should().Contain(t => t.Key == "k8s.namespace.name" && Convert.ToString(t.Value) == "blogs",
            "OTEL_RESOURCE_ATTRIBUTES adds resource attributes");
    }

    private static ActivityTraceId AttachTrace(HttpRequestMessage request)
    {
        var traceId = ActivityTraceId.CreateRandom();
        request.Headers.TryAddWithoutValidation("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01");
        return traceId;
    }

    private static string PathOf(Activity span)
        => (span.GetTagItem("url.path") ?? span.GetTagItem("http.target") ?? span.DisplayName)?.ToString() ?? "";

    private WebApplicationFactory<Program> CreateFactory(
        Action<IServiceCollection>? configureServices,
        string? otlpEndpoint,
        string? remoteIp = null,
        IReadOnlyDictionary<string, string?>? extraConfig = null)
    {
        // The telemetry setup reads OTEL_* while the host is being configured, so the tests set
        // them through UseSetting (early, like any environment variable) rather than the late
        // in-memory configuration — that one is not visible yet at setup time.
        var factory = new BlogApiFactory(
            fixture.ConnectionString,
            storagePath: Path.Combine(Path.GetTempPath(), "croft-api-telemetry", Guid.NewGuid().ToString("N")),
            configuration: null,
            configureServices: services =>
            {
                if (remoteIp is not null)
                {
                    services.AddSingleton<IStartupFilter>(new RemoteIpStartupFilter(IPAddress.Parse(remoteIp)));
                }

                configureServices?.Invoke(services);
            });

        return factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("OTEL_EXPORTER_OTLP_ENDPOINT", otlpEndpoint ?? "");
            if (extraConfig is not null)
            {
                foreach (var (key, value) in extraConfig)
                {
                    builder.UseSetting(key, value);
                }
            }
        });
    }
}

internal static class ApiTelemetryTestExtensions
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
