using Croft.Api.Services;
using Croft.Api.Tests.Fixtures;
using AwesomeAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Croft.Api.Tests;

/// <summary>
/// Brief 13c item 5: the two mechanisms that hold the public site up are visible on the Croft.Api meter —
/// the read cache (croft.cache.requests by outcome, croft.cache.invalidations by group,
/// croft.cache.refills by connection) and its listener (croft.cache.listener.connected gauge,
/// croft.cache.listener.reconnects counter). Attribute values stay in fixed sets.
/// </summary>
[Collection(nameof(CroftMetricsGroup))]
public class CacheMetricsTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    [Fact]
    public async Task AMissThenAHit_AreCountedAsRequestsByOutcome()
    {
        using var metrics = new MetricCapture("Croft.Api").Start();
        var cache = new MemoryPublicReadCache(new MemoryCache(new MemoryCacheOptions()));

        await cache.GetOrCreateAsync(CacheGroup.Posts, "posts:metrics", _ => Task.FromResult("v"));
        await cache.GetOrCreateAsync(CacheGroup.Posts, "posts:metrics", _ => Task.FromResult("v"));

        Requests(metrics, "miss").Should().Be(1);
        Requests(metrics, "hit").Should().Be(1);
    }

    [Fact]
    public async Task Invalidations_AreCountedPerGroup_AndAClearAsAll()
    {
        using var metrics = new MetricCapture("Croft.Api").Start();
        var cache = new MemoryPublicReadCache(new MemoryCache(new MemoryCacheOptions()));

        await cache.InvalidateAsync(CacheGroup.Posts | CacheGroup.Tags);
        await cache.ClearAsync();

        Invalidations(metrics, "posts").Should().Be(1);
        Invalidations(metrics, "tags").Should().Be(1);
        Invalidations(metrics, "categories").Should().Be(0, "only the groups named are counted");
        Invalidations(metrics, "all").Should().Be(1, "a clear (the listener's reconnect) is its own series");
    }

    [Fact]
    public async Task Refills_AreCountedByTheConnectionTheyReadFrom()
    {
        using var metrics = new MetricCapture("Croft.Api").Start();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = new MemoryPublicReadCache(new MemoryCache(new MemoryCacheOptions()), time);

        await cache.GetOrCreateAsync(CacheGroup.Posts, "posts:never-invalidated", _ => Task.FromResult("v"));
        await cache.InvalidateAsync(CacheGroup.Posts);
        await cache.GetOrCreateAsync(CacheGroup.Posts, "posts:right-after", _ => Task.FromResult("v"));
        time.Advance(TimeSpan.FromSeconds(61));
        await cache.GetOrCreateAsync(CacheGroup.Posts, "posts:after-window", _ => Task.FromResult("v"));

        Refills(metrics, "primary").Should().Be(1, "only the refill inside the window is forced to the primary");
        Refills(metrics, "default").Should().Be(2, "the others use the normal routing (the replica for an anonymous read when one is configured)");
    }

    [Fact]
    public async Task TheListener_ReportsItsConnectionState_AndCountsReconnects()
    {
        using var metrics = new MetricCapture("Croft.Api").Start();
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);
        await using var factory = new BlogApiFactory(
            fixture.ConnectionString,
            storagePath,
            configureServices: services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<CacheListenerService>());
            });
        var listener = factory.Services.GetRequiredService<CacheListenerService>();
        await WaitAsync(() => listener.BackendPid is not null, "the listener connects");

        ConnectedNow(metrics).Should().Be(1, "one connected listener");
        Reconnects(metrics).Should().Be(0, "the first connection is not a reconnect");

        var firstPid = listener.BackendPid!.Value;
        await using (var admin = new NpgsqlConnection(fixture.ConnectionString))
        {
            await admin.OpenAsync();
            await using var kill = admin.CreateCommand();
            kill.CommandText = "SELECT pg_terminate_backend(@pid)";
            kill.Parameters.AddWithValue("pid", firstPid);
            await kill.ExecuteScalarAsync();
        }

        await WaitAsync(() => Reconnects(metrics) >= 1, "the listener counts its reconnect");
        await WaitAsync(() => listener.BackendPid is { } pid && pid != firstPid, "the listener reconnects on a new backend");
        Reconnects(metrics).Should().Be(1);
        ConnectedNow(metrics).Should().Be(1, "connected again after the reconnect");
    }

    /// <summary>One collection pass over the gauge: its value now, not the history of earlier passes.</summary>
    private static double ConnectedNow(MetricCapture m)
    {
        var before = m.Measurements.Count;
        m.Observe();
        return m.Measurements.Skip(before).Where(x => x.Instrument == "croft.cache.listener.connected").Sum(x => x.Value);
    }

    private static double Requests(MetricCapture m, string outcome) => Sum(m, "croft.cache.requests", "outcome", outcome);

    private static double Invalidations(MetricCapture m, string group) => Sum(m, "croft.cache.invalidations", "group", group);

    private static double Refills(MetricCapture m, string source) => Sum(m, "croft.cache.refills", "source", source);

    private static double Reconnects(MetricCapture m) => m.Measurements.Where(x => x.Instrument == "croft.cache.listener.reconnects").Sum(x => x.Value);

    private static double Sum(MetricCapture m, string instrument, string tag, string value) =>
        m.Measurements.Where(x => x.Instrument == instrument && x.HasTag(tag, value)).Sum(x => x.Value);

    private static async Task WaitAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Yield();
        condition().Should().BeTrue(what);
    }
}
