using Croft.Shared.Warmup;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Croft.Api.Services;

/// <summary>
/// Readiness while the pod warms itself up (brief 14a): Unhealthy ("Warming up") until the
/// <see cref="WarmupGate"/> opens, which it always does when the warm-up finishes, fails or runs
/// out of time. Liveness never includes it: a restart cannot speed a warm-up up.
/// </summary>
public sealed class WarmupHealthCheck(WarmupGate gate) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(gate.IsOpen ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Warming up"));
}
