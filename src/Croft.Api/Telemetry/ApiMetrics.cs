using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Croft.Api.Telemetry;

/// <summary>
/// Croft's own counters on the Croft.Api meter (subscribed by the shared telemetry setup): what
/// the owner's dashboards read. Each method is one event and pins its attribute values, so the
/// number of series stays small.
/// </summary>
public static class ApiMetrics
{
    private static readonly Meter Meter = new("Croft.Api");

    private static readonly Counter<long> PageViews = Meter.CreateCounter<long>(
        "croft.page_views.recorded",
        unit: "{page_view}",
        description: "Page-view beacons, by deduplication outcome.");

    private static readonly Counter<long> PostsPublished = Meter.CreateCounter<long>(
        "croft.posts.published",
        unit: "{post}",
        description: "Posts that went live, by who published them.");

    private static readonly Counter<long> CacheRequests = Meter.CreateCounter<long>(
        "croft.cache.requests",
        unit: "{request}",
        description: "Public read cache lookups, by outcome: hit, or miss (the entry was missing, expired, or a not-found/empty one past its 30 s).");

    private static readonly Counter<long> CacheInvalidations = Meter.CreateCounter<long>(
        "croft.cache.invalidations",
        unit: "{invalidation}",
        description: "Cache groups dropped on this instance, by group (categories, tags, posts, pages); `all` is the whole cache cleared, as the listener does on every (re)connect.");

    private static readonly Counter<long> CacheRefills = Meter.CreateCounter<long>(
        "croft.cache.refills",
        unit: "{refill}",
        description: "Cache refills (the database reads behind misses), by source: `primary` when forced to the primary because the group was invalidated within Croft:Api:Cache:PrimaryAfterInvalidation, `default` for the normal routing (the read replica for an anonymous read when Database:ReadHost is set).");

    private static readonly Counter<long> ListenerReconnects = Meter.CreateCounter<long>(
        "croft.cache.listener.reconnects",
        unit: "{reconnect}",
        description: "Times the cache listener reconnected after losing its connection (each one clears the whole cache); the first connection is not counted.");

    private static readonly ConcurrentDictionary<object, Func<bool>> Listeners = new();

    static ApiMetrics() =>
        Meter.CreateObservableGauge(
            "croft.cache.listener.connected",
            () => Listeners.Values.Count(connected => connected()),
            unit: "{connection}",
            description: "Cache listeners currently connected to Postgres: 1 per API process when healthy, 0 while it is reconnecting (edits made then reach this instance only by the 5-minute expiry).");

    /// <summary>A public read cache lookup found its entry.</summary>
    public static void CacheHit() => CacheRequests.Add(1, Tag("outcome", "hit"));

    /// <summary>A public read cache lookup did not find its entry.</summary>
    public static void CacheMiss() => CacheRequests.Add(1, Tag("outcome", "miss"));

    /// <summary>One cache group (or, as `all`, the whole cache) was dropped.</summary>
    public static void CacheInvalidated(string group) => CacheInvalidations.Add(1, Tag("group", group));

    /// <summary>A refill ran against the database.</summary>
    public static void CacheRefill(bool forcedToPrimary) => CacheRefills.Add(1, Tag("source", forcedToPrimary ? "primary" : "default"));

    /// <summary>The cache listener reconnected after a lost connection.</summary>
    public static void CacheListenerReconnected() => ListenerReconnects.Add(1);

    /// <summary>Registers a listener's connection state for the gauge; dispose it when the listener stops.</summary>
    public static IDisposable ObserveCacheListener(Func<bool> isConnected)
    {
        var key = new object();
        Listeners[key] = isConnected;
        return new Registration(key);
    }

    private sealed class Registration(object key) : IDisposable
    {
        public void Dispose() => Listeners.TryRemove(key, out _);
    }

    /// <summary>A page view stored: the only outcome that counts towards the view count.</summary>
    public static void PageViewStored() => PageViews.Add(1, Tag("outcome", "recorded"));

    /// <summary>A repeat the unique index turned away: counted so the dashboard can see the
    /// deduplication at work, but never added to `recorded`.</summary>
    public static void PageViewDuplicate() => PageViews.Add(1, Tag("outcome", "duplicate"));

    /// <summary>An editor made a post live from the publish endpoint.</summary>
    public static void PostPublishedByEditor() => PostsPublished.Add(1, Tag("source", "editor"));

    /// <summary>The background publisher made a due scheduled post live.</summary>
    public static void PostPublishedByScheduler() => PostsPublished.Add(1, Tag("source", "scheduled"));

    private static KeyValuePair<string, object?> Tag(string key, string value) => new(key, value);
}
