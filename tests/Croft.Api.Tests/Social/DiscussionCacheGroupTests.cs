using AwesomeAssertions;
using Croft.Api.Services;
using Microsoft.Extensions.Caching.Memory;

namespace Croft.Api.Tests.Social;

/// <summary>Brief 12a: the Discussion cache group is a group of its own, outside All, and travels as "discussion" in the notifications.</summary>
public class DiscussionCacheGroupTests
{
    [Fact]
    public void TheNotificationPayload_NamesTheGroupInLowerCase()
    {
        CacheNotifier.Payload(CacheGroup.Discussion).Should().Be("discussion");
        CacheNotifier.Payload(CacheGroup.All | CacheGroup.Discussion).Should().Be("categories,tags,posts,pages,discussion");
        CacheNotifier.Payload(CacheGroup.All).Should().Be("categories,tags,posts,pages", "All does not include the discussions");
    }

    [Fact]
    public void ThePayload_IsReadBack()
    {
        CacheNotifier.TryParsePayload("discussion", out var one).Should().BeTrue();
        one.Should().Be(CacheGroup.Discussion);
        CacheNotifier.TryParsePayload("posts,discussion", out var both).Should().BeTrue();
        both.Should().Be(CacheGroup.Posts | CacheGroup.Discussion);
    }

    [Fact]
    public void APodThatDoesNotKnowTheName_IgnoresTheWholeNotification()
    {
        // A pod of the release before brief 12a reads "discussion" as an unknown name: TryParsePayload is false and the listener
        // logs and ignores the payload (it serves no discussion, so there is nothing of it to clear).
        CacheNotifier.TryParsePayload("discussion-from-a-newer-release", out var groups).Should().BeFalse();
        groups.Should().Be(default(CacheGroup));
        CacheNotifier.TryParsePayload("posts,something-new", out _).Should().BeFalse("one unknown name makes the whole payload unknown, exactly as an older pod reads it");
    }

    [Fact]
    public async Task InvalidatingTheDiscussionGroup_DropsOnlyItsEntries()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions { SizeLimit = 100 });
        using var cache = new MemoryPublicReadCache(memory);
        await cache.GetOrCreateAsync(CacheGroup.Discussion, "discussion:a", _ => Task.FromResult("discussion"));
        await cache.GetOrCreateAsync(CacheGroup.Posts, "posts:a", _ => Task.FromResult("post"));

        await cache.InvalidateAsync(CacheGroup.Discussion);

        var refills = 0;
        await cache.GetOrCreateAsync(CacheGroup.Discussion, "discussion:a", _ => { refills++; return Task.FromResult("discussion"); });
        await cache.GetOrCreateAsync(CacheGroup.Posts, "posts:a", _ => { refills++; return Task.FromResult("post"); });
        refills.Should().Be(1, "only the discussion entry was dropped");
    }

    [Fact]
    public async Task InvalidatingAll_LeavesTheDiscussionEntriesAlone()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions { SizeLimit = 100 });
        using var cache = new MemoryPublicReadCache(memory);
        await cache.GetOrCreateAsync(CacheGroup.Discussion, "discussion:a", _ => Task.FromResult("discussion"));

        await cache.InvalidateAsync(CacheGroup.All);

        var refills = 0;
        await cache.GetOrCreateAsync(CacheGroup.Discussion, "discussion:a", _ => { refills++; return Task.FromResult("discussion"); });
        refills.Should().Be(0, "a post save must not flush the discussions");
    }
}
