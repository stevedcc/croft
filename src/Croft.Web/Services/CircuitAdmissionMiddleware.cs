using Croft.Web.Options;

namespace Croft.Web.Services;

/// <summary>
/// Admission control for circuits. Circuits are for signed-in staff only: a request that would
/// START one without an authenticated user is refused outright, whatever the count (anonymous
/// readers are on static pages and never need one). At or above Croft:Web:Circuits:MaxActive circuits in the
/// process (0 = no limit), a request that would START a circuit gets 503 with Retry-After: 5
/// instead of adding one more. The reader keeps the prerendered content and loses only
/// interactivity. Existing circuits are never refused: any request carrying an id (a reconnect,
/// a re-negotiate, a transport poll) goes through whatever the count is. Closing one always
/// goes through too — the /_blazor/disconnect beacon is not a circuit start — so the count can
/// fall back under the ceiling without operator help. Every refusal is one warning line: a
/// burst of 503s on /_blazor is otherwise inexplicable beside the gauge.
/// </summary>
internal sealed partial class CircuitAdmissionMiddleware(
    RequestDelegate next,
    CircuitCounter counter,
    CircuitSettings settings,
    ILogger<CircuitAdmissionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var startsCircuit = WouldStartCircuit(context);
        if (startsCircuit && context.User.Identity?.IsAuthenticated != true)
        {
            LogAnonymousCircuitStartRefused(logger);
            await RefuseAsync(context);
            return;
        }

        if (startsCircuit
            && settings.MaxActive > 0
            && counter.Count >= settings.MaxActive)
        {
            LogCircuitStartRefused(logger, settings.MaxActive, counter.Count);
            await RefuseAsync(context);
            return;
        }

        await next(context);
    }

    private static async Task RefuseAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = "5";
        await context.Response.WriteAsync("Circuit limit reached");
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Circuit start refused: the request is not signed in (circuits are for staff)")]
    private static partial void LogAnonymousCircuitStartRefused(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Circuit start refused: at Croft:Web:Circuits:MaxActive ({maxActive}) with {count} circuits open")]
    private static partial void LogCircuitStartRefused(ILogger logger, int maxActive, long count);

    // WORKAROUND(W-001, Blazor): circuit starts are recognised by the negotiate POST or an id-less WebSocket upgrade; /_blazor/disconnect and id= requests are not starts
    private static bool WouldStartCircuit(HttpContext context)
    {
        var request = context.Request;
        if (!request.Path.StartsWithSegments("/_blazor") || request.Query.ContainsKey("id"))
        {
            return false;
        }

        // POST /_blazor/negotiate opens a negotiated connection; older clients skip negotiate and
        // WebSocket straight to /_blazor. Both start a circuit; anything else without an id —
        // notably the /_blazor/disconnect beacon — is not a start.
        return (HttpMethods.IsPost(request.Method) && request.Path.Equals("/_blazor/negotiate"))
            || (HttpMethods.IsGet(request.Method)
                && string.Equals(request.Headers.Upgrade, "websocket", StringComparison.OrdinalIgnoreCase));
    }
    // END WORKAROUND(W-001)
}
