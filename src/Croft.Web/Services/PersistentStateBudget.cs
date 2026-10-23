// WORKAROUND-FILE(W-010, SignalR): persisted page state rides the hub start message, so state over the budget is not handed over (the 2 MiB hub limit)
using System.Text.Json;

namespace Croft.Web.Services;

/// <summary>
/// How much a page may hand from its prerender to its circuit with <c>[PersistentState]</c>.
/// The state travels in the HTML and back over the SignalR hub inside one message, encoded about
/// 10.7 times per JSON byte (JSON bytes as base64 inside the state dictionary, protected, base64
/// again), and the hub accepts 2 MiB (W-010). A 200,000-character post of <c>€</c> signs is 1.2 MB
/// of JSON and took the circuit down, so state over the budget is not handed over: the circuit
/// fetches it again, as it did before the pages persisted anything.
/// </summary>
public static class PersistentStateBudget
{
    /// <summary>The JSON bytes a page may persist: about 1.6 MB encoded in the worst case, inside the hub limit.</summary>
    public const int MaxJsonBytes = 150_000;

    /// <summary>Whether <paramref name="state"/> serialises to no more than <see cref="MaxJsonBytes"/>.</summary>
    public static bool Fits<T>(T state) =>
        JsonSerializer.SerializeToUtf8Bytes(state, JsonSerializerOptions.Web).Length <= MaxJsonBytes;
}
