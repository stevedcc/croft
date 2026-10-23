using Microsoft.Extensions.Caching.Memory;
using System.Collections.Concurrent;
using Croft.Api.Data;
using Croft.Api.Telemetry;

namespace Croft.Api.Services;

/// <summary>
/// The cache namespaces a write invalidates. The Postgres notification payloads are the
/// lower-case names: <c>categories</c>, <c>tags</c>, <c>posts</c>, <c>pages</c> and <c>discussion</c>.
/// </summary>
[Flags]
public enum CacheGroup
{
    Categories = 1,
    Tags = 2,
    Posts = 4,
    Pages = 8,

    /// <summary>
    /// The social replies below posts (brief 12a). Deliberately not part of <see cref="All"/>: a post save must not
    /// flush the discussions, and a discussion refresh must not flush the posts.
    /// </summary>
    Discussion = 16,

    /// <summary>
    /// Every content write invalidates all four groups at once — one rule is safer than a map
    /// of which write affects which read (writes are rare).
    /// </summary>
    All = Categories | Tags | Posts | Pages
}

/// <summary>The single cache groups, in slot order.</summary>
public static class CacheGroups
{
    public static readonly CacheGroup[] Singles = [CacheGroup.Categories, CacheGroup.Tags, CacheGroup.Posts, CacheGroup.Pages, CacheGroup.Discussion];

    /// <summary>
    /// What a post appearing, disappearing or going away invalidates: everything that shows it, and its discussion (an unpublished post's
    /// discussion must answer 404 at once). An ordinary content edit stays at <see cref="CacheGroup.All"/>.
    /// </summary>
    public const CacheGroup PostVisibility = CacheGroup.All | CacheGroup.Discussion;
}

/// <summary>
/// Per-instance cache for the public reads — the anonymous variants of the post/page reads
/// and the category/tag reads that carry published-post counts. The write path invalidates
/// the affected groups locally at once and via
/// <see cref="ICacheNotifier"/> on every other replica; the 5-minute expiry is only a safety net
/// for notifications that got lost.
/// </summary>
public interface IPublicReadCache
{
    Task<T> GetOrCreateAsync<T>(CacheGroup group, string key, Func<CancellationToken, Task<T>> factory, CancellationToken ct = default);

    /// <summary>As above; <paramref name="isEmpty"/> marks a non-null result that counts as "nothing found" (an empty page), cached like a null one, briefly.</summary>
    Task<T> GetOrCreateAsync<T>(CacheGroup group, string key, Func<CancellationToken, Task<T>> factory, Func<T, bool>? isEmpty, CancellationToken ct = default);

    /// <summary>Drops the entries of every group in <paramref name="groups"/> (one or several), under one lock acquisition.</summary>
    Task InvalidateAsync(CacheGroup groups, CancellationToken ct = default);

    Task ClearAsync(CancellationToken ct = default);
}

/// <summary>
/// Keeps every live key with its group so invalidation can drop the exact entries —
/// <see cref="IMemoryCache"/> cannot enumerate its keys.
/// </summary>
public sealed class MemoryPublicReadCache(
    IMemoryCache cache,
    TimeProvider? timeProvider = null,
    TimeSpan? primaryAfterInvalidation = null,
    int maxNegativeEntries = MemoryPublicReadCache.DefaultMaxNegativeEntries) : IPublicReadCache, IDisposable
{
    /// <summary>How long a not-found or empty result is cached.</summary>
    public static readonly TimeSpan NegativeExpiry = TimeSpan.FromSeconds(30);

    /// <summary>At most this many not-found/empty entries live at once: a crawler asking for random
    /// slugs or terms can never take more of the cache than this.</summary>
    public const int DefaultMaxNegativeEntries = 200;

    public static readonly TimeSpan Expiry = TimeSpan.FromMinutes(5);

    /// <summary><c>Croft:Api:Cache:PrimaryAfterInvalidation</c> default: how long after a group's
    /// invalidation its refills read the primary.</summary>
    public static readonly TimeSpan DefaultPrimaryAfterInvalidation = TimeSpan.FromSeconds(60);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    private readonly TimeSpan _primaryAfterInvalidation = primaryAfterInvalidation ?? DefaultPrimaryAfterInvalidation;

    /// <summary>When each group was last invalidated (UTC ticks; 0 = never). A refill within
    /// <c>PrimaryAfterInvalidation</c> of it reads the primary: the replica may not have replayed
    /// the write that caused the invalidation (architecture review 2026-10-19, finding 2). The
    /// group, not the key, is the unit: invalidation drops every key of its group, and a first
    /// refill of another key in that group is no more likely to be wrong to read from the primary.</summary>
    private readonly long[] _invalidatedAt = new long[CacheGroups.Singles.Length];

    private readonly ConcurrentDictionary<string, CacheGroup> _keys = new();

    /// <summary>
    /// One generation counter per group. InvalidateAsync/ClearAsync bump it and a refill
    /// whose factory ran across that bump — a read begun before a write committed, finishing
    /// after the invalidation — is not stored: it would put the pre-write value back and keep
    /// it until the next write or the expiry. The caller still gets the value it fetched.
    /// </summary>
    private readonly int[] _generations = new int[CacheGroups.Singles.Length];

    /// <summary>
    /// Makes the generation check-and-store and the generation bump mutually exclusive: an
    /// invalidation can no longer land between a refill's check and its store. Awaitable
    /// everywhere — there is no blocking wait — and held only across in-memory work.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Test hook: awaited between the factory and the locked check-and-store.</summary>
    public Func<Task>? BeforeStore { get; set; }

    public Task<T> GetOrCreateAsync<T>(CacheGroup group, string key, Func<CancellationToken, Task<T>> factory, CancellationToken ct = default) =>
        GetOrCreateAsync(group, key, factory, null, ct);

    /// <summary>Not-found and empty results, held apart from the real entries by their wrapper.</summary>
    private sealed record Negative(object? Value, long ExpiresAtTicks);

    /// <summary>One refill in progress for a key, and the generation of its group when it began.</summary>
    private sealed record Refill(int Generation, Task<object?> Result);

    private readonly ConcurrentDictionary<string, Refill> _refills = new();

    private int _negativeCount;

    /// <summary>The not-found and empty entries held now (for the budget tests).</summary>
    public int NegativeCount => Volatile.Read(ref _negativeCount);

    public async Task<T> GetOrCreateAsync<T>(CacheGroup group, string key, Func<CancellationToken, Task<T>> factory, Func<T, bool>? isEmpty, CancellationToken ct = default)
    {
        var counted = false;
        while (true)
        {
            if (TryRead(key, out T? cached))
            {
                if (!counted)
                    ApiMetrics.CacheHit();
                return cached!;
            }

            if (!counted)
            {
                ApiMetrics.CacheMiss();
                counted = true;
            }

            var slot = Slot(group);
            var generation = Volatile.Read(ref _generations[slot]);
            var mine = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var refill = new Refill(generation, mine.Task);

            // One refill per key at a time: concurrent misses (the stampede after an invalidation)
            // wait for the first one's result. A refill that began before an invalidation is not
            // shared with a miss that began after it — its result predates the write.
            var running = _refills.GetOrAdd(key, refill);
            if (!ReferenceEquals(running, refill))
            {
                if (running.Generation == generation)
                {
                    try
                    {
                        return (T)(await running.Result.WaitAsync(ct))!;
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // The first reader was cancelled (its request ended): this one refills instead.
                        continue;
                    }
                }

                _refills[key] = refill;
            }

            try
            {
                var value = await RefillAsync(group, slot, generation, key, factory, isEmpty, ct);
                mine.TrySetResult(value);
                return value;
            }
            catch (OperationCanceledException ex)
            {
                mine.TrySetCanceled(ex.CancellationToken);
                throw;
            }
            catch (Exception ex)
            {
                mine.TrySetException(ex);
                _ = mine.Task.Exception; // observed: nobody may be waiting
                throw;
            }
            finally
            {
                _refills.TryRemove(new KeyValuePair<string, Refill>(key, refill));
            }
        }
    }

    private bool TryRead<T>(string key, out T? value)
    {
        value = default;
        if (!cache.TryGetValue(key, out var raw))
            return false;

        if (raw is Negative negative)
        {
            if (_time.GetUtcNow().UtcTicks >= negative.ExpiresAtTicks)
                return false;
            raw = negative.Value;
        }

        if (raw is null)
            return true;

        if (raw is not T typed)
            return false;

        value = typed;
        return true;
    }

    private async Task<T> RefillAsync<T>(
        CacheGroup group, int slot, int generation, string key, Func<CancellationToken, Task<T>> factory, Func<T, bool>? isEmpty, CancellationToken ct)
    {
        T value;
        var forcedToPrimary = ReadsFromPrimary(slot);
        ApiMetrics.CacheRefill(forcedToPrimary);
        using (forcedToPrimary ? PrimaryRefill.Begin() : null)
        {
            value = await factory(ct);
        }

        if (BeforeStore is not null)
            await BeforeStore();

        await _gate.WaitAsync(ct);
        try
        {
            if (Volatile.Read(ref _generations[slot]) == generation)
            {
                var negative = value is null || (isEmpty?.Invoke(value) ?? false);
                if (!negative || Volatile.Read(ref _negativeCount) < maxNegativeEntries)
                    Store(group, key, value, negative);
            }
        }
        finally
        {
            _gate.Release();
        }

        return value;
    }

    /// <summary>Stores under the gate. A not-found or empty result lives <see cref="NegativeExpiry"/>, at low priority and
    /// within <c>maxNegativeEntries</c>, so a flood of misses can neither pin the cache nor push out the real entries.</summary>
    private void Store(CacheGroup group, string key, object? value, bool negative)
    {
        var options = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = negative ? NegativeExpiry : Expiry,
            Priority = negative ? CacheItemPriority.Low : CacheItemPriority.Normal,
            Size = 1,
        };
        object stored = negative
            ? new Negative(value, _time.GetUtcNow().UtcTicks + NegativeExpiry.Ticks)
            : value!;
        if (negative)
            Interlocked.Increment(ref _negativeCount);

        // WORKAROUND(W-079, .NET): EvictionReason.Replaced fires after the newer Set already owns the key, so the callback must not drop its tracking
        options.RegisterPostEvictionCallback((evictedKey, evicted, _, _) =>
        {
            if (evicted is Negative)
                Interlocked.Decrement(ref _negativeCount);

            // EvictionReason.Replaced fires while the newer Set already owns this key —
            // concurrent refills of one key are the common case after an invalidation.
            // The newer Set's tracking must survive, or invalidation would miss the live
            // entry and serve stale content past every content write until expiry.
            if (cache.TryGetValue(evictedKey, out var current) && !ReferenceEquals(current, evicted))
                return;
            _keys.TryRemove((string)evictedKey, out _);
        });
        // END WORKAROUND(W-079)
        cache.Set(key, stored, options);
        _keys[key] = group;
    }

    public async Task InvalidateAsync(CacheGroup groups, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = _time.GetUtcNow().UtcTicks;
            foreach (var group in CacheGroups.Singles.Where(group => groups.HasFlag(group)))
            {
                ApiMetrics.CacheInvalidated(group.ToString().ToLowerInvariant());
                var slot = Slot(group);
                Interlocked.Increment(ref _generations[slot]);
                Volatile.Write(ref _invalidatedAt[slot], now);
            }

            foreach (var (key, keyGroup) in _keys)
            {
                if (groups.HasFlag(keyGroup) && _keys.TryRemove(key, out _))
                    cache.Remove(key);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ApiMetrics.CacheInvalidated("all");
            for (var slot = 0; slot < _generations.Length; slot++)
            {
                Interlocked.Increment(ref _generations[slot]);
                Volatile.Write(ref _invalidatedAt[slot], _time.GetUtcNow().UtcTicks);
            }

            foreach (var key in _keys.Keys)
            {
                if (_keys.TryRemove(key, out _))
                    cache.Remove(key);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    private bool ReadsFromPrimary(int slot)
    {
        var invalidatedAt = Volatile.Read(ref _invalidatedAt[slot]);
        return invalidatedAt != 0
            && _time.GetUtcNow().UtcTicks - invalidatedAt < _primaryAfterInvalidation.Ticks;
    }

    /// <summary>Invalidation takes one cache group at a time; the notifier decomposes composites.</summary>
    private static int Slot(CacheGroup group) => group switch
    {
        CacheGroup.Categories => 0,
        CacheGroup.Tags => 1,
        CacheGroup.Posts => 2,
        CacheGroup.Pages => 3,
        CacheGroup.Discussion => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(group), "one cache group at a time"),
    };

    /// <summary>The number of live entries the cache is holding (for the bound tests).</summary>
    public int Count => _keys.Count;
}
