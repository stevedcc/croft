namespace Croft.Web.Services;

/// <summary>
/// The refresh path's serialisation point across Web instances, implemented beside the store it
/// protects (see <see cref="PostgresTokenStore"/> and <see cref="InMemoryTokenStore"/>). The work
/// runs while holding an exclusive lock on one session: it sees the session's entry as re-read
/// under that lock, and its writes are applied before the lock is released. Two instances
/// refreshing one session therefore serialise — the loser waits, re-reads the winner's stored
/// token and takes it, instead of spending the same rotating refresh token twice and having it
/// revoked at the IdP.
/// </summary>
public interface ISessionLock
{
    /// <summary>
    /// Runs <paramref name="work"/> while holding an exclusive lock on
    /// <paramref name="sessionId"/>'s entry. <see cref="LockedSession.Entry"/> is the entry as
    /// re-read under the lock (null when the session is gone); its
    /// <see cref="LockedSession.SetAsync"/>/<see cref="LockedSession.RemoveAsync"/> are applied
    /// and committed before the lock is released.
    /// </summary>
    Task<T> RunLockedAsync<T>(
        string sessionId, Func<LockedSession, CancellationToken, Task<T>> work, CancellationToken ct = default);
}
