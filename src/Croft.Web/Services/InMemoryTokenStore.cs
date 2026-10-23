using System.Collections.Concurrent;

namespace Croft.Web.Services;

/// <summary>
/// The single-instance default (no ConnectionStrings:WebDb): sessions live in this process's
/// memory. It trivially satisfies <see cref="ISessionLock"/> — with one instance the refresh
/// path's per-session semaphore already serialises refreshes, so the locked work can simply run.
/// An entry past its <see cref="TokenEntry.SessionExpiresAt"/> (Auth:SessionLifetime, or the refresh
/// token's lifetime) reads as missing, so the cookie is rejected and nothing is ever refreshed for
/// it; <see cref="PurgeExpired"/> (run every minute by <see cref="InMemorySessionPurgeHostedService"/>)
/// frees the memory.
/// </summary>
public sealed class InMemoryTokenStore(TimeProvider? time = null) : ITokenStore, ISessionLock
{
    private readonly ConcurrentDictionary<string, TokenEntry> _entries = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The number of entries held, expired ones included until the purge removes them.</summary>
    public int Count => _entries.Count;

    /// <summary>Removes every entry whose session has ended; returns how many.</summary>
    public int PurgeExpired()
    {
        var now = _time.GetUtcNow();
        return RemoveWhere(entry => IsExpired(entry, now));
    }

    public Task<TokenEntry?> GetAsync(string sessionId, CancellationToken ct = default) =>
        Task.FromResult(Get(sessionId));

    public Task SetAsync(string sessionId, TokenEntry entry, CancellationToken ct = default)
    {
        Set(sessionId, entry);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string sessionId, CancellationToken ct = default)
    {
        Remove(sessionId);
        return Task.CompletedTask;
    }

    public Task<int> RemoveBySidAsync(string sid, CancellationToken ct = default) =>
        Task.FromResult(RemoveWhere(entry => entry.Sid == sid));

    public Task<int> RemoveBySubAsync(string sub, CancellationToken ct = default) =>
        Task.FromResult(RemoveWhere(entry => entry.Sub == sub));

    public Task<T> RunLockedAsync<T>(
        string sessionId, Func<LockedSession, CancellationToken, Task<T>> work, CancellationToken ct = default) =>
        work(new LockedSession(Get(sessionId), (entry, _) =>
        {
            if (entry is null)
            {
                Remove(sessionId);
            }
            else
            {
                Set(sessionId, entry);
            }

            return Task.CompletedTask;
        }), ct);

    private TokenEntry? Get(string sessionId) =>
        _entries.TryGetValue(sessionId, out var entry) && !IsExpired(entry, _time.GetUtcNow()) ? entry : null;

    private static bool IsExpired(TokenEntry entry, DateTimeOffset now) => entry.SessionExpiresAt < now;

    private void Set(string sessionId, TokenEntry entry) => _entries[sessionId] = entry;

    private void Remove(string sessionId) => _entries.TryRemove(sessionId, out _);

    private int RemoveWhere(Func<TokenEntry, bool> matches)
    {
        var removed = 0;
        foreach (var (sessionId, entry) in _entries)
        {
            if (matches(entry) && _entries.TryRemove(sessionId, out _))
            {
                removed++;
            }
        }

        return removed;
    }
}
