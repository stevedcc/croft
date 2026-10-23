using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Croft.Shared.Telemetry;
using Croft.Web.Tests.Fixtures;
using AwesomeAssertions;

namespace Croft.Web.Tests;

/// <summary>
/// The .NET 10 Blazor and SignalR meters in Web: a circuit that opens must be counted by
/// aspnetcore.components.circuit.active, and the meter names the setup subscribes must be the
/// exact names .NET 10 emits (listed here against a MeterListener while real traffic runs).
/// </summary>
public class BlazorTelemetryTests
{
    [Fact]
    public async Task CircuitOpen_RecordsTheBlazorCircuitMetric()
    {
        var capture = new TelemetryCapture();
        using var factory = WebTelemetryTests.CreateFactory(capture, signedIn: true);
        using var client = factory.CreateClient();

        // A circuit opens with the components the prerendered page carries — without them Blazor
        // defers the whole open sequence (and its metrics) to a later update. The reading pages are
        // static SSR since brief 09f and carry no markers, so the circuit opens on an admin page; circuits
        // are for signed-in staff only (brief 13a), so the factory signs every request in.
        using var page = await client.GetAsync("/admin/posts");
        page.EnsureSuccessStatusCode();
        var markers = BlazorCircuitClient.CollectSsrComponentMarkers(await page.Content.ReadAsStringAsync());
        markers.Should().NotBe("[]", "the prerendered page must carry interactive markers to open a circuit with");

        await using var circuit = await BlazorCircuitClient.ConnectAsync(client);
        var secret = await circuit.InvokeAsync("StartCircuit", "http://localhost/admin/posts", "http://localhost/admin/posts", markers, "");
        secret.Should().NotBeNullOrEmpty("a circuit must actually open here or the metric proves nothing");

        await capture.WaitForMetricAsync(
            factory.Services,
            m => m.Name == "aspnetcore.components.circuit.active"
                && m.MeterName == "Microsoft.AspNetCore.Components.Server.Circuits",
            "the Blazor circuit counter after a circuit opened");
    }

    [Fact]
    public async Task BlazorAndSignalrMeterNames_MatchWhatDotNet10Emits()
    {
        var emitted = new ConcurrentBag<string>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, _) => emitted.Add(instrument.Meter.Name),
        };
        meterListener.Start();

        var capture = new TelemetryCapture();
        using var factory = WebTelemetryTests.CreateFactory(capture, signedIn: true);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/admin/posts");
        response.EnsureSuccessStatusCode();
        var markers = BlazorCircuitClient.CollectSsrComponentMarkers(await response.Content.ReadAsStringAsync());
        await using var circuit = await BlazorCircuitClient.ConnectAsync(client);
        await circuit.InvokeAsync("StartCircuit", "http://localhost/admin/posts", "http://localhost/admin/posts", markers, "");

        var blazorAndSignalr = emitted.Distinct()
            .Where(name => name.StartsWith("Microsoft.AspNetCore.Components", StringComparison.Ordinal)
                || name.StartsWith("Microsoft.AspNetCore.SignalR", StringComparison.Ordinal)
                || name.StartsWith("Microsoft.AspNetCore.Http.Connections", StringComparison.Ordinal))
            .Order()
            .ToList();

        // The exact meter names .NET 10 emits for Blazor and SignalR, listed here so a framework
        // change shows up as a failing list rather than as silently missing dashboards.
        blazorAndSignalr.Should().Equal(
            "Microsoft.AspNetCore.Components",
            "Microsoft.AspNetCore.Components.Lifecycle",
            "Microsoft.AspNetCore.Components.Server.Circuits",
            "Microsoft.AspNetCore.Http.Connections");

        blazorAndSignalr.Should().OnlyContain(
            name => CroftTelemetry.SubscribedFrameworkMeters(CroftApp.Web).Contains(name),
            "the telemetry setup must subscribe every meter the framework emits");
    }
}
