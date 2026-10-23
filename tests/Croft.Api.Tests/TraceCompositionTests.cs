using System.Diagnostics;
using Croft.Api.Tests.Fixtures;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Croft.Api.Tests;

/// <summary>
/// One reader request across both apps: the public page on Web calls the API over HttpClient, and
/// the API queries the database. The whole journey must be one trace — Web server span, HttpClient
/// span, API server span, Npgsql span — with each span the child of the one that caused it. That
/// chain is the proof that W3C traceparent propagation works between the apps. The API host is
/// in-memory, so the bridge gives it a real loopback socket: on that socket HttpClient runs the
/// production transport, which is where client spans and traceparent injection live.
/// </summary>
public class TraceCompositionTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    [Fact]
    public async Task PublicPageOnWeb_WithApiCall_YieldsOneTrace_WithEverySpanAChildOfItsCaller()
    {
        var capture = new TelemetryCapture();
        using var api = new BlogApiFactory(
            fixture.ConnectionString,
            storagePath: Path.Combine(Path.GetTempPath(), "croft-api-trace", Guid.NewGuid().ToString("N")))
            .WithWebHostBuilder(builder =>
                builder.UseSetting("OTEL_EXPORTER_OTLP_ENDPOINT", TelemetryCapture.EnabledEndpoint));

        await using var bridge = new LoopbackApiBridge(api.Server.CreateHandler());

        // Both hosts run in this process, so every ActivityListener sees both apps' spans; the
        // capture hangs off the Web host only and therefore holds each span exactly once.
        using var web = new WebApplicationFactory<Croft.Web.Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", bridge.Url.ToString());
            builder.UseSetting("Blog:BaseUrl", "https://blog.test/");
            builder.UseSetting("OTEL_EXPORTER_OTLP_ENDPOINT", TelemetryCapture.EnabledEndpoint);
            builder.ConfigureServices(capture.Register);
        });

        // A known trace id scopes the assertions to this request: xUnit runs test classes in
        // parallel in this process and their spans land in the same capture.
        var traceId = ActivityTraceId.CreateRandom();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.TryAddWithoutValidation("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01");
        using var client = web.CreateClient();
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        bool InTrace(Activity span) => span.TraceId == traceId;

        var webServer = await capture.WaitForSpanAsync(
            s => InTrace(s) && s.Kind == ActivityKind.Server && PathOf(s) == "/",
            "the Web server span for GET /");
        var apiServer = await capture.WaitForSpanAsync(
            s => InTrace(s) && s.Kind == ActivityKind.Server && PathOf(s).StartsWith("/api/posts", StringComparison.Ordinal),
            "the API server span for the posts request the home page makes");

        var httpClientSpan = capture.Spans.Single(s => s.SpanId == apiServer.ParentSpanId);
        httpClientSpan.Kind.Should().Be(ActivityKind.Client,
            "the API is reached over HttpClient from Web");
        httpClientSpan.TraceId.Should().Be(traceId,
            "the whole reader request must be one trace");
        httpClientSpan.ParentSpanId.Should().Be(webServer.SpanId,
            "the outbound call belongs to the request that made it");
        apiServer.ParentSpanId.Should().Be(httpClientSpan.SpanId,
            "the API span must be a child of the HttpClient span, not a new root — that is what the "
            + "traceparent header the client sends does");

        var npgsql = capture.Spans.Where(s => InTrace(s) && s.Source.Name == "Npgsql").ToList();
        npgsql.Should().Contain(s => s.ParentSpanId == apiServer.SpanId,
            "the SQL the API runs belongs to the API request that ran it");
        bridge.Errors.Should().BeEmpty("the bridge must relay cleanly or the spans prove nothing");
    }

    private static string PathOf(Activity span)
        => (span.GetTagItem("url.path") ?? span.GetTagItem("http.target") ?? span.DisplayName)?.ToString() ?? "";
}
