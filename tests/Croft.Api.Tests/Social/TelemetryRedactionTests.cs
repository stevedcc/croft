using System.Diagnostics;
using System.Net;
using AwesomeAssertions;
using Croft.Shared.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;

namespace Croft.Api.Tests.Social;

/// <summary>Brief 12a item 4: the span of an avatar download exports its host only; an avatar's path can carry a handle.</summary>
public class TelemetryRedactionTests
{
    [Fact]
    public void AFlaggedRequest_IsRewrittenToItsHost()
    {
        using var activity = new Activity("test");
        activity.SetTag("url.full", "https://cdn.example/img/avatar/plain/did:plc:abc/cid@jpeg");
        activity.SetTag("url.path", "/img/avatar/plain/did:plc:abc/cid@jpeg");
        activity.SetTag("server.address", "cdn.example");
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://cdn.example/img/avatar/plain/did:plc:abc/cid@jpeg");
        request.Options.Set(TelemetryRedaction.HostOnly, true);

        TelemetryRedaction.Apply(activity, request);

        activity.GetTagItem("url.full").Should().Be("https://cdn.example/");
        activity.GetTagItem("url.path").Should().Be("/");
        activity.GetTagItem("server.address").Should().Be("cdn.example");
    }

    [Fact]
    public void AnUnflaggedRequest_IsLeftAlone()
    {
        using var activity = new Activity("test");
        activity.SetTag("url.full", "https://cdn.example/a/b");
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://cdn.example/a/b");

        TelemetryRedaction.Apply(activity, request);

        activity.GetTagItem("url.full").Should().Be("https://cdn.example/a/b");
    }

    [Fact]
    public async Task ThroughTheRealHttpClientInstrumentation_TheExportedSpanHasNoPath()
    {
        using var listener = new HttpListener();
        var port = FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var server = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            context.Response.StatusCode = 200;
            context.Response.Close();
        });

        List<Activity> exported = [];
        var services = new ServiceCollection();
        services.AddOpenTelemetry().WithTracing(tracing => tracing
            .AddHttpClientInstrumentation(CroftTelemetry.ConfigureHttpClientInstrumentation)
            .AddInMemoryExporter(exported));
        await using var provider = services.BuildServiceProvider();
        var tracerProvider = provider.GetRequiredService<TracerProvider>();

        using (var client = new HttpClient(new SocketsHttpHandler()))
        using (var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/accounts/some-handle/avatar.png"))
        {
            request.Options.Set(TelemetryRedaction.HostOnly, true);
            (await client.SendAsync(request)).EnsureSuccessStatusCode();
        }

        await server;
        tracerProvider.ForceFlush();

        var span = exported.Should().ContainSingle().Subject;
        span.TagObjects.Select(t => t.Value?.ToString() ?? string.Empty).Should().NotContain(v => v.Contains("some-handle", StringComparison.Ordinal),
            "no exported attribute of an avatar request carries its path");
        span.GetTagItem("url.full").Should().Be($"http://127.0.0.1:{port}/");
    }

    private static int FreePort()
    {
        using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }
}
