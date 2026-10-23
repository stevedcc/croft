// WORKAROUND-FILE(W-043, Blazor): no cookie validation runs inside a circuit, so the editor must back up from the 401 path before the forced login redirect
using Microsoft.JSInterop;

namespace Croft.Web.Services;

/// <summary>
/// What a page does just before the circuit sends the browser to sign in again because the
/// session is gone (<see cref="BlogApiClient"/> found a 401 for an authenticated user): the post
/// editor writes its draft backup now, while the browser is still on the page. One per circuit.
/// </summary>
public sealed class SessionExpiryHooks
{
    private readonly Lock _gate = new();
    private readonly List<Func<Task<bool>>> _hooks = [];

    /// <summary>
    /// Registers work to run before the sign-in redirect. The work returns whether it kept
    /// something worth restoring after sign-in. Dispose the registration when the page goes away.
    /// </summary>
    public IDisposable Register(Func<Task<bool>> beforeRedirect)
    {
        ArgumentNullException.ThrowIfNull(beforeRedirect);
        lock (_gate)
        {
            _hooks.Add(beforeRedirect);
        }

        return new Registration(this, beforeRedirect);
    }

    /// <summary>
    /// Runs every registered hook and says whether any of them kept work to restore. A hook that
    /// fails (the circuit or the browser is already gone) counts as keeping nothing; it never
    /// stops the redirect.
    /// </summary>
    public async Task<bool> RunAsync()
    {
        Func<Task<bool>>[] hooks;
        lock (_gate)
        {
            hooks = [.. _hooks];
        }

        var kept = false;
        foreach (var hook in hooks)
        {
            try
            {
                kept |= await hook();
            }
            catch (Exception ex) when (ex is InvalidOperationException or JSException or JSDisconnectedException or TaskCanceledException)
            {
                // The redirect matters more than the backup.
            }
        }

        return kept;
    }

    private void Remove(Func<Task<bool>> hook)
    {
        lock (_gate)
        {
            _hooks.Remove(hook);
        }
    }

    private sealed class Registration(SessionExpiryHooks owner, Func<Task<bool>> hook) : IDisposable
    {
        public void Dispose() => owner.Remove(hook);
    }
}
