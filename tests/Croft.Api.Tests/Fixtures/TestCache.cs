using Croft.Api.Services;
using Microsoft.Extensions.Caching.Memory;

namespace Croft.Api.Tests.Fixtures;

/// <summary>
/// A real cache and notifier for tests that construct services directly instead of going
/// through the factory: the notifier's pg_notify lands in the test database, where no listener
/// is waiting, which is exactly what those tests need.
/// </summary>
internal static class TestCache
{
    public static IPublicReadCache NewCache() =>
        new MemoryPublicReadCache(new MemoryCache(new MemoryCacheOptions()));

    public static ICacheNotifier NewNotifier() => new CacheNotifier(NewCache());
}
