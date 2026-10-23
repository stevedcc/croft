using System.Diagnostics.Metrics;

namespace Croft.Web.Telemetry;

/// <summary>
/// Croft's own counters on the Croft.Web meter (subscribed by the shared telemetry setup): what
/// the owner's dashboards read. Each method is one event and pins its attribute values, so the
/// number of series stays small.
/// </summary>
public static class WebMetrics
{
    private static readonly Meter Meter = new("Croft.Web");

    private static readonly Counter<long> CspReports = Meter.CreateCounter<long>(
        "croft.csp.reports",
        unit: "{report}",
        description: "Reported CSP violations, by effective directive.");

    private static readonly Counter<long> TokenRefreshes = Meter.CreateCounter<long>(
        "croft.token.refreshes",
        unit: "{refresh}",
        description: "Access-token refresh attempts, by outcome.");

    private static readonly Counter<long> SessionsRemoved = Meter.CreateCounter<long>(
        "croft.web.session.removed",
        unit: "{session}",
        description: "Login sessions that ended, by reason.");

    /// <summary>A reported CSP violation under its effective directive — the blocked URI and the
    /// document path can carry reader data and are never attributes.</summary>
    public static void CspViolationReported(string directive)
        => CspReports.Add(1, new KeyValuePair<string, object?>("directive", directive));

    /// <summary>A refresh that stored new tokens.</summary>
    public static void TokenRefreshSucceeded() => TokenRefreshes.Add(1, Outcome("refreshed"));

    /// <summary>
    /// The live circuit count (connected plus disconnected ones still retained), read when the
    /// collector collects. A process has one Web host and one counter, so the gauge carries one
    /// observer at a time; <see cref="IDisposable.Dispose"/> unregisters it (the tests that read
    /// the gauge run serialised — CroftMetricsGroup — so exactly one is registered while they do).
    /// </summary>
    public static IDisposable PublishActiveCircuits(Func<long> observe)
    {
        ActiveCircuits = observe;
        ActiveCircuitsGauge ??= Meter.CreateObservableGauge<long>(
            "croft.web.circuits.active",
            () => ActiveCircuits?.Invoke() ?? 0,
            unit: "{circuit}",
            description: "Circuits in this process: connected plus disconnected ones still retained.");
        return new Registration(observe);
    }

    private static Func<long>? ActiveCircuits;

    private static ObservableGauge<long>? ActiveCircuitsGauge;

    /// <summary>Unregisters an observer — only if it is still the current one, so a stale
    /// registration's disposal cannot silence a newer counter's gauge.</summary>
    private sealed class Registration(Func<long> observe) : IDisposable
    {
        public void Dispose()
        {
            if (ReferenceEquals(ActiveCircuits, observe))
            {
                ActiveCircuits = null;
            }
        }
    }

    /// <summary>The IdP had a bad moment; the session is kept and the current token is used.</summary>
    public static void TokenRefreshTransientFailure() => TokenRefreshes.Add(1, Outcome("transient_failure"));

    /// <summary>The refresh token was rejected (or there was none): the session is gone.</summary>
    public static void TokenRefreshFailed() => TokenRefreshes.Add(1, Outcome("failed"));

    /// <summary>Sessions that ended for <paramref name="reason"/> (one of <c>SessionRemoval</c>'s fixed reasons).</summary>
    public static void SessionRemoved(string reason, long count = 1)
    {
        if (count > 0)
        {
            SessionsRemoved.Add(count, new KeyValuePair<string, object?>("reason", reason));
        }
    }

    private static KeyValuePair<string, object?> Outcome(string value) => new("outcome", value);
}
