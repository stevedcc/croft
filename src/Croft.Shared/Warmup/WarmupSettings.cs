namespace Croft.Shared.Warmup;

/// <summary>
/// The <c>Warmup</c> configuration section, the same for both apps: a pod warms itself up by
/// sending its own public read requests to itself before it reports ready (brief 14a).
/// </summary>
public sealed class WarmupSettings
{
    public const string SectionName = "Warmup";

    /// <summary>Whether the pod warms up before it reports ready. Off: the readiness gate starts open.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The whole warm-up's budget. When it runs out the gate opens regardless.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Each request's own budget; a request that takes longer counts as failed.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(15);
}
