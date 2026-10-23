using System.Runtime.CompilerServices;

namespace Croft.Api.Tests;

/// <summary>
/// Brief 14a: every host these tests start has the warm-up off, as the shared set-up for every
/// <c>WebApplicationFactory</c> in the suite. The warm-up would otherwise add a readiness
/// condition and send requests nothing here expects. The warm-up's own tests turn it on in their
/// host's configuration, which is read after the environment and so wins.
/// </summary>
internal static class WarmupDisabledByDefault
{
    [ModuleInitializer]
    internal static void Run() => Environment.SetEnvironmentVariable("Warmup__Enabled", "false");
}
