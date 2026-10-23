using System.Net;
using System.Text.Json;
using Croft.Web.Tests.Fixtures;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests;

/// <summary>
/// Brief 09g item 2: admission control for circuits. A process holds at most
/// Croft:Web:Circuits:MaxActive circuits (connected and disconnected); at the ceiling, requests
/// that would START a circuit (POST /_blazor/negotiate, or the no-negotiate WebSocket handshake)
/// get 503 with Retry-After: 5. Reconnects (any request carrying an id) are never refused, and
/// the prerendered page stays readable whatever the count. The count itself is published as the
/// croft.web.circuits.active gauge on Croft's Web meter.
/// </summary>
[Collection(nameof(CroftMetricsGroup))]
public class CircuitAdmissionTests
{
    [Fact]
    public async Task Negotiate_AtMaxActive_Gets503WithRetryAfter()
    {
        using var factory = CreateFactory(settings => settings["Croft:Web:Circuits:MaxActive"] = "2");
        using var http = factory.CreateClient();
        await using var one = await OpenCircuitAsync(http);
        await using var two = await OpenCircuitAsync(http);

        using var negotiate = await http.PostAsync("/_blazor/negotiate?negotiateVersion=1", content: null);

        negotiate.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "at MaxActive no additional circuit may start");
        negotiate.Headers.RetryAfter?.Delta.Should().Be(TimeSpan.FromSeconds(5),
            "the client is told when to try again instead of hammering the pod");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("400")]
    public async Task Negotiate_FromAnAnonymousRequest_IsRefused_WhateverTheCount(string maxActive)
    {
        using var factory = CreateFactory(settings => settings["Croft:Web:Circuits:MaxActive"] = maxActive, signedIn: false);
        using var http = factory.CreateClient();

        using var negotiate = await http.PostAsync("/_blazor/negotiate?negotiateVersion=1", content: null);

        negotiate.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "circuits are for signed-in staff: an anonymous visitor never starts one");
        (await negotiate.Content.ReadAsStringAsync()).Should().Be("Circuit limit reached");
    }

    [Fact]
    public async Task WebSocketWithoutId_FromAnAnonymousRequest_IsRefused()
    {
        using var factory = CreateFactory(signedIn: false);
        using var http = factory.CreateClient();

        using var handshake = new HttpRequestMessage(HttpMethod.Get, "/_blazor");
        handshake.Headers.TryAddWithoutValidation("Connection", "Upgrade");
        handshake.Headers.TryAddWithoutValidation("Upgrade", "websocket");
        handshake.Headers.TryAddWithoutValidation("Sec-WebSocket-Version", "13");
        handshake.Headers.TryAddWithoutValidation("Sec-WebSocket-Key", "dGhlIHNhbXBsZSBub25jZQ==");
        using var probe = await http.SendAsync(handshake);

        probe.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Negotiate_FromASignedInRequest_BelowTheCount_StillStartsACircuit()
    {
        using var factory = CreateFactory();
        using var http = factory.CreateClient();
        await using var circuit = await OpenCircuitAsync(http);

        circuit.CircuitId.Should().NotBeNullOrEmpty("a signed-in staff member opens circuits as before");
    }

    [Fact]
    public async Task Negotiate_BelowMaxActive_PassesOnToSignalR()
    {
        using var factory = CreateFactory(settings => settings["Croft:Web:Circuits:MaxActive"] = "2");
        using var http = factory.CreateClient();
        await using var one = await OpenCircuitAsync(http);

        using var negotiate = await http.PostAsync("/_blazor/negotiate?negotiateVersion=1", content: null);

        negotiate.StatusCode.Should().Be(HttpStatusCode.OK,
            "below MaxActive a new circuit start goes through to SignalR");
        using var document = JsonDocument.Parse(await negotiate.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("connectionToken").GetString().Should().NotBeNullOrEmpty(
            "the response is SignalR's negotiate payload, not an admission refusal");
    }

    [Fact]
    public async Task RequestWithId_IsNeverRefused()
    {
        using var factory = CreateFactory(settings => settings["Croft:Web:Circuits:MaxActive"] = "2");
        using var http = factory.CreateClient();
        await using var one = await OpenCircuitAsync(http);
        await using var two = await OpenCircuitAsync(http);

        // A reconnect carries the connection id: SignalR re-negotiates with ?id=..., and a
        // transport request carries it too. At the ceiling or not, those requests must reach
        // SignalR — an existing circuit can always come back.
        using var negotiateReconnect = await http.PostAsync("/_blazor/negotiate?negotiateVersion=1&id=stale-token", content: null);
        using var transportReconnect = await http.GetAsync("/_blazor?id=stale-token");

        negotiateReconnect.StatusCode.Should().NotBe(HttpStatusCode.ServiceUnavailable,
            "a reconnect's negotiate carries an id and is never refused");
        transportReconnect.StatusCode.Should().NotBe(HttpStatusCode.ServiceUnavailable,
            "a reconnect's transport request carries an id and is never refused");
    }

    [Fact]
    public async Task WebSocketWithoutId_AtMaxActive_IsRefused()
    {
        using var factory = CreateFactory(settings => settings["Croft:Web:Circuits:MaxActive"] = "1");
        using var http = factory.CreateClient();
        await using var one = await OpenCircuitAsync(http);

        // The no-negotiate path: older clients WebSocket straight to /_blazor and start a
        // circuit there — admission applies to it exactly as to POST /_blazor/negotiate.
        using var handshake = new HttpRequestMessage(HttpMethod.Get, "/_blazor");
        handshake.Headers.TryAddWithoutValidation("Connection", "Upgrade");
        handshake.Headers.TryAddWithoutValidation("Upgrade", "websocket");
        handshake.Headers.TryAddWithoutValidation("Sec-WebSocket-Version", "13");
        handshake.Headers.TryAddWithoutValidation("Sec-WebSocket-Key", "dGhlIHNhbXBsZSBub25jZQ==");
        using var probe = await http.SendAsync(handshake);

        probe.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "at MaxActive a WebSocket without an id would start a circuit and is refused");
        probe.Headers.RetryAfter?.Delta.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task MaxActiveZero_NeverRefuses()
    {
        using var factory = CreateFactory(settings => settings["Croft:Web:Circuits:MaxActive"] = "0");
        using var http = factory.CreateClient();
        await using var one = await OpenCircuitAsync(http);
        await using var two = await OpenCircuitAsync(http);

        using var negotiate = await http.PostAsync("/_blazor/negotiate?negotiateVersion=1", content: null);

        negotiate.StatusCode.Should().Be(HttpStatusCode.OK,
            "MaxActive 0 is the no-limit switch whatever the count is");
        using var document = JsonDocument.Parse(await negotiate.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("connectionToken").GetString().Should().NotBeNullOrEmpty(
            "the response is SignalR's negotiate payload, not an admission refusal");
    }

    [Fact]
    public async Task CircuitOpenedAndClosed_MoveThePublishedGauge()
    {
        using var metrics = new MetricCapture("Croft.Web").Start();
        using var factory = CreateFactory();
        using var http = factory.CreateClient();

        await using var circuit = await OpenCircuitAsync(http);
        LatestCircuitGauge(metrics).Should().Be(1, "an opened circuit counts from its open event");

        using var close = await http.PostAsync("/_blazor/disconnect",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["circuitId"] = circuit.CircuitId }));
        close.IsSuccessStatusCode.Should().BeTrue("the browser's goodbye closes the circuit at once");

        LatestCircuitGauge(metrics).Should().Be(0,
            "a closed circuit stops counting from its close event — and only then");
    }

    private static double LatestCircuitGauge(MetricCapture metrics)
    {
        metrics.RecordObservable();
        var readings = metrics.Measurements.Where(m => m.Instrument == "croft.web.circuits.active").ToList();
        readings.Should().NotBeEmpty("the croft.web.circuits.active gauge must publish the count");
        return readings[^1].Value;
    }

    /// <summary>Opens a real circuit through the hub (BlazorCircuitClient) and returns it with
    /// the circuit id StartCircuit reports.</summary>
    private static async Task<OpenCircuit> OpenCircuitAsync(HttpClient http)
    {
        using var page = await http.GetAsync("/admin/posts");
        page.EnsureSuccessStatusCode();
        var markers = BlazorCircuitClient.CollectSsrComponentMarkers(await page.Content.ReadAsStringAsync());
        var circuit = await BlazorCircuitClient.ConnectAsync(http);
        var circuitId = await circuit.InvokeAsync("StartCircuit", "http://localhost/admin/posts", "http://localhost/admin/posts", markers, "");
        return new OpenCircuit(circuit, circuitId);
    }

    private sealed class OpenCircuit(BlazorCircuitClient client, string circuitId) : IAsyncDisposable
    {
        public string CircuitId { get; } = circuitId;

        public ValueTask DisposeAsync() => client.DisposeAsync();
    }

    private static WebApplicationFactory<Program> CreateFactory(
        Action<IDictionary<string, string?>>? configure = null, bool signedIn = true)
    {
        Dictionary<string, string?> settings = [];
        settings["ApiBaseUrl"] = "http://blog-api.test/";
        settings["Blog:BaseUrl"] = "https://blog.test/";
        configure?.Invoke(settings);

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureServices(services =>
            {
                services.AddHttpClient("BlogApi").ConfigurePrimaryHttpMessageHandler(() => new StubBlogApiHandler());
                if (signedIn)
                {
                    SignedInAdmin.AddTo(services);
                }
            });
        });
    }
}
