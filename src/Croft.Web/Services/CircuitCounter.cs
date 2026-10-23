using Croft.Web.Telemetry;

namespace Croft.Web.Services;

/// <summary>
/// The process's circuit count: opened minus closed. A disconnected circuit keeps counting
/// until Blazor closes it (its retention ended, or its browser said goodbye through
/// /_blazor/disconnect) — until then it is still memory this process holds. The count gates new
/// circuits (CircuitAdmissionMiddleware) and is published as croft.web.circuits.active.
/// </summary>
internal sealed class CircuitCounter : IDisposable
{
    private readonly IDisposable _activeGauge;
    private long _count;

    public CircuitCounter()
    {
        _activeGauge = WebMetrics.PublishActiveCircuits(() => Count);
    }

    /// <summary>Circuits open right now: connected, and disconnected ones still retained.</summary>
    public long Count => Interlocked.Read(ref _count);

    public void CircuitOpened() => Interlocked.Increment(ref _count);

    public void CircuitClosed() => Interlocked.Decrement(ref _count);

    // A disposed counter stops publishing, so a torn-down host's readings stay out of the meter.
    public void Dispose() => _activeGauge.Dispose();
}
