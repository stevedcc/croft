using System.Runtime.CompilerServices;

namespace Croft.Web.Tests;

/// <summary>
/// Brief 14a: every host these tests start has the warm-up off, as the shared set-up for every
/// <c>WebApplicationFactory</c> in the suite (there are dozens, each building its own). The
/// warm-up would otherwise close <c>/health/ready</c> and send requests nothing here expects.
/// The warm-up's own tests turn it on in their host's configuration, which is read after the
/// environment and so wins.
/// </summary>
internal static class WarmupDisabledByDefault
{
    [ModuleInitializer]
    internal static void Run() => Environment.SetEnvironmentVariable("Warmup__Enabled", "false");
}
