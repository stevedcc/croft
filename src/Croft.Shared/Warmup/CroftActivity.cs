using System.Diagnostics;

namespace Croft.Shared.Warmup;

/// <summary>Croft's own <see cref="ActivitySource"/>, subscribed by <c>AddCroftTelemetry</c>.</summary>
public static class CroftActivity
{
    public const string SourceName = "Croft";

    public static readonly ActivitySource Source = new(SourceName);

    /// <summary>The Activity the warm-up's requests all run inside, so they form one trace.</summary>
    public const string WarmupName = "croft.warmup";
}
