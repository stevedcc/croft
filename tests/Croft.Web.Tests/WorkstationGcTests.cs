using System.Text.Json;
using AwesomeAssertions;

namespace Croft.Web.Tests;

/// <summary>
/// Brief 09g item 3: Workstation GC is croft-web's default (a site can still choose Server GC
/// with DOTNET_gcServer=1). Under a container memory limit .NET already caps the GC heap at 75%
/// of it — the OOM kills came from memory outside the heap — and Workstation GC's smaller
/// baseline (76-79 Mi fresh vs 167-189 Mi with Server GC, 2026-10-15) leaves more of the pod's
/// limit for circuits. The built Croft.Web.runtimeconfig.json is the oracle: the test host's own
/// GCSettings.IsServerGC reflects the test runner, not croft-web.
/// </summary>
public class WorkstationGcTests
{
    [Fact]
    public void CroftWebRuntimeConfig_TurnsServerGarbageCollectionOff()
    {
        var runtimeConfig = Path.Combine(AppContext.BaseDirectory, "Croft.Web.runtimeconfig.json");
        using var document = JsonDocument.Parse(File.ReadAllText(runtimeConfig));
        var configProperties = document.RootElement
            .GetProperty("runtimeOptions")
            .GetProperty("configProperties");

        configProperties.TryGetProperty("System.GC.Server", out var serverGc).Should().BeTrue(
            "croft-web's runtimeconfig must pin its GC mode explicitly");
        serverGc.GetBoolean().Should().BeFalse(
            "Workstation GC is croft-web's default; DOTNET_gcServer=1 is the site's opt-in");
    }
}
