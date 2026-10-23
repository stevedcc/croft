using Croft.Web.Options;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Croft.Web.Tests;

/// <summary>
/// Brief 09g item 1: the disconnected-circuit retention is a Croft setting with modest defaults
/// (20 retained for 60 s — Blazor's 100 per server for 3 minutes kept every reload's dead circuit
/// alive beside the live ones, and a reload never reconnects to its old circuit). An invalid
/// value (non-integer, negative) fails at startup naming the setting.
/// </summary>
public class CircuitLimitTests
{
    [Fact]
    public void Defaults_KeepTwentyDisconnectedCircuitsForSixtySeconds()
    {
        using var factory = CreateFactory();

        var circuit = factory.Services.GetRequiredService<IOptions<CircuitOptions>>().Value;

        circuit.DisconnectedCircuitMaxRetained.Should().Be(20,
            "the modest default replaces Blazor's 100-per-server retention");
        circuit.DisconnectedCircuitRetentionPeriod.Should().Be(TimeSpan.FromSeconds(60),
            "one minute is enough for a reader's brief network blip and no longer");
        factory.Services.GetRequiredService<CircuitSettings>().MaxActive.Should().Be(400,
            "the circuit ceiling's default matches the Contracts table exactly");
    }

    [Fact]
    public void ConfiguredRetention_IsApplied()
    {
        using var factory = CreateFactory(settings =>
        {
            settings["Croft:Web:Circuits:DisconnectedMaxRetained"] = "5";
            settings["Croft:Web:Circuits:DisconnectedRetentionSeconds"] = "15";
        });

        var circuit = factory.Services.GetRequiredService<IOptions<CircuitOptions>>().Value;

        circuit.DisconnectedCircuitMaxRetained.Should().Be(5, "the setting binds into CircuitOptions");
        circuit.DisconnectedCircuitRetentionPeriod.Should().Be(TimeSpan.FromSeconds(15),
            "the setting binds into CircuitOptions");
    }

    [Theory]
    [InlineData("Croft:Web:Circuits:DisconnectedMaxRetained")]
    [InlineData("Croft:Web:Circuits:DisconnectedRetentionSeconds")]
    [InlineData("Croft:Web:Circuits:MaxActive")]
    public void NegativeValue_FailsStartup_NamingTheSetting(string setting)
        => AssertStartupFailure(setting, "-1");

    [Theory]
    [InlineData("Croft:Web:Circuits:DisconnectedMaxRetained", "abc")]
    [InlineData("Croft:Web:Circuits:DisconnectedRetentionSeconds", "abc")]
    [InlineData("Croft:Web:Circuits:MaxActive", "abc")]
    [InlineData("Croft:Web:Circuits:MaxActive", " ")]
    public void NonIntegerValue_FailsStartup_NamingTheSetting(string setting, string value)
        => AssertStartupFailure(setting, value);

    private static void AssertStartupFailure(string setting, string value)
    {
        using var factory = CreateFactory(settings => settings[setting] = value);

        var act = () => factory.CreateClient();

        var error = act.Should().Throw<InvalidOperationException>().Which;
        error.ToString().Should().Contain(setting,
            $"an invalid {setting} must fail at startup naming the setting, not silently start");
    }

    private static WebApplicationFactory<Program> CreateFactory(
        Action<IDictionary<string, string?>>? configure = null)
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
        });
    }
}
