// WORKAROUND-FILE(W-001, Blazor): the count drops only when Blazor closes a circuit (retention expiry or the disconnect beacon)
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Croft.Web.Services;

/// <summary>
/// Moves the process circuit counter as circuits open and close. Blazor closes a circuit when
/// it is gone for good — retention ended or the browser's /_blazor/disconnect beacon — and a
/// merely disconnected circuit keeps counting until then.
/// </summary>
internal sealed class CircuitCountingHandler(CircuitCounter counter) : CircuitHandler
{
    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        counter.CircuitOpened();
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        counter.CircuitClosed();
        return Task.CompletedTask;
    }
}
