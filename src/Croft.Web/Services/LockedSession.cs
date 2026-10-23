namespace Croft.Web.Services;

/// <summary>
/// A session's entry while its lock is held (see <see cref="ISessionLock"/>):
/// <see cref="Entry"/> is the entry as re-read under the lock, and the writes below go to the
/// locked session — they are applied and committed before the lock is released.
/// </summary>
public sealed class LockedSession(TokenEntry? entry, Func<TokenEntry?, CancellationToken, Task> apply)
{
    public TokenEntry? Entry { get; } = entry;

    /// <summary>Replaces the session's entry.</summary>
    public Task SetAsync(TokenEntry entry, CancellationToken ct = default) => apply(entry, ct);

    /// <summary>Removes the session.</summary>
    public Task RemoveAsync(CancellationToken ct = default) => apply(null, ct);
}
