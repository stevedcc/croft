using Microsoft.JSInterop;

namespace Croft.Web.Services;

/// <summary>Releases a JavaScript module a component imported, when the component goes away.</summary>
public static class JsModuleReference
{
    // WORKAROUND(W-042, Blazor): a prerendered instance is disposed where JS interop is forbidden; circuit shutdown cancels or throws
    /// <summary>
    /// Disposes <paramref name="module"/> if the component ever imported it. A component that never
    /// rendered interactively has none (a static or prerendered instance is disposed where JS interop is
    /// forbidden), and a circuit that is already gone cannot release it, which is not an error.
    /// </summary>
    public static async ValueTask DisposeAsync(IJSObjectReference? module)
    {
        if (module is null)
        {
            return;
        }

        try
        {
            await module.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
            // The circuit is gone; the browser side went with it.
        }
        catch (TaskCanceledException)
        {
            // The circuit is shutting down; the call is cancelled before it runs.
        }
    }
    // END WORKAROUND(W-042)
}
