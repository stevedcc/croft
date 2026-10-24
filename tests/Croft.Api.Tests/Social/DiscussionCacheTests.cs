using System.Net;
using AwesomeAssertions;
using Croft.Api.Data;
using Croft.Api.Services;
using Croft.Api.Tests.Fixtures;
using Croft.Api.Tests.Fixtures.Social;
using Croft.Shared.Contracts;
using Croft.Shared.Contracts.Social;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static Croft.Api.Tests.Fixtures.Social.DiscussionSeed;

namespace Croft.Api.Tests.Social;

/// <summary>
/// Brief 12a item 5, caching: the anonymous read is cached in the Discussion group (key per slug); the 48-hour rule is applied AFTER the cache; a staff
/// read is never cached; the Discussion group is not part of All, so a post save does not evict a discussion, while a link change, a publish, an unpublish and a
/// delete do.
/// </summary>
public class DiscussionCacheTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private static readonly DateTimeOffset Start = new(Noon.AddHours(1));

    private static async Task<(PostDto Post, BlogApiFactory Factory)> SeededAsync(PostgresDatabaseFixture fixture, FakeTimeProvider time, SqlCommandCounter counter)
    {
        var factory = SocialApiFactory.Create(fixture, time: time, commandInterceptor: counter);
        var post = await PostAsync(factory.CreateClient().WithRole("Admin"), $"Cached discussion {Guid.NewGuid():N}");
        await StoreAsync(factory, post.Id, SocialPlatform.Mastodon, Noon, new ReplySpec("m1", null, Noon));
        return (post, factory);
    }

    [Fact]
    public async Task TheSecondAnonymousRead_RunsNoSql_AndReturnsTheSameDiscussion()
    {
        var counter = new SqlCommandCounter();
        var (post, factory) = await SeededAsync(fixture, new FakeTimeProvider(Start), counter);
        await using var _ = factory;
        var anonymous = factory.CreateClient().AsAnonymous();

        var first = await ReadAsync(anonymous, post.Slug);
        counter.Reset();
        var second = await ReadAsync(anonymous, post.Slug);

        counter.Commands.Should().Be(0, "the second identical anonymous read comes from the Discussion cache group");
        second.Should().BeEquivalentTo(first);
        Shape(second.Replies).Should().Equal("m1");
    }

    [Fact]
    public async Task TheStaffReads_AreNeverCached()
    {
        var counter = new SqlCommandCounter();
        var (post, factory) = await SeededAsync(fixture, new FakeTimeProvider(Start), counter);
        await using var _ = factory;
        var admin = factory.CreateClient().WithRole("Admin");

        foreach (var url in new[] { $"/api/posts/{post.Slug}/discussion", $"/api/posts/{post.Id}/discussion/admin" })
        {
            (await admin.GetAsync(url)).EnsureSuccessStatusCode();
            counter.Reset();
            (await admin.GetAsync(url)).EnsureSuccessStatusCode();
            counter.Commands.Should().BeGreaterThan(0, $"{url} read by staff runs its queries every time");
        }
    }

    [Fact]
    public async Task The48HourRule_IsAppliedAfterTheCache_ACachedEntryCannotOutliveIt()
    {
        var time = new FakeTimeProvider(Start);
        var counter = new SqlCommandCounter();
        var (post, factory) = await SeededAsync(fixture, time, counter);
        await using var _ = factory;
        var anonymous = factory.CreateClient().AsAnonymous();
        Shape((await ReadAsync(anonymous, post.Slug)).Replies).Should().Equal("m1");

        time.Advance(TimeSpan.FromHours(46)); // 47 h after the last success: still shown
        counter.Reset();
        Shape((await ReadAsync(anonymous, post.Slug)).Replies).Should().Equal("m1");
        counter.Commands.Should().Be(0, "it is the cached entry that is served");

        time.Advance(TimeSpan.FromHours(2)); // 49 h, with no write and no invalidation in between
        counter.Reset();
        var stale = await ReadAsync(anonymous, post.Slug);

        counter.Commands.Should().Be(0, "still the cached entry, not a fresh query: the rule is applied on the way out");
        stale.Replies.Should().BeEmpty("the last successful fetch is older than 48 hours");
        stale.ReplyCount.Should().Be(0);
        stale.MastodonUrl.Should().Be(MastodonLink);
    }

    [Fact]
    public async Task APostSave_DoesNotEvictACachedDiscussion_ButALinkChangeDoes()
    {
        var counter = new SqlCommandCounter();
        var (post, factory) = await SeededAsync(fixture, new FakeTimeProvider(Start), counter);
        await using var _ = factory;
        var admin = factory.CreateClient().WithRole("Admin");
        var anonymous = factory.CreateClient().AsAnonymous();
        await ReadAsync(anonymous, post.Slug);

        var edited = await admin.PutAsJsonAsync($"/api/posts/{post.Id}",
            new UpdatePostRequest("Cached discussion, edited", "edited body", MastodonUrl: MastodonLink, BlueskyUrl: BlueskyLink));
        edited.EnsureSuccessStatusCode();
        counter.Reset();
        await ReadAsync(anonymous, post.Slug);
        counter.Commands.Should().Be(0, "a post save invalidates All, which does not include Discussion");

        var relinked = await admin.PutAsJsonAsync($"/api/posts/{post.Id}",
            new UpdatePostRequest("Cached discussion, edited", "edited body", MastodonUrl: "https://mastodon.example/@owner/2222", BlueskyUrl: BlueskyLink));
        relinked.EnsureSuccessStatusCode();
        counter.Reset();
        var discussion = await ReadAsync(anonymous, post.Slug);

        counter.Commands.Should().BeGreaterThan(0, "a link change invalidates the Discussion group");
        discussion.MastodonUrl.Should().Be("https://mastodon.example/@owner/2222");
    }

    [Fact]
    public void All_DoesNotContainTheDiscussionGroup() =>
        CacheGroup.All.HasFlag(CacheGroup.Discussion).Should().BeFalse("a post save must not flush the discussions, and a refresh must not flush the posts");

    [Fact]
    public async Task UnpublishingAndPublishing_FlipTheCachedDiscussionAtOnce_AndDeletingRemovesIt()
    {
        var counter = new SqlCommandCounter();
        var (post, factory) = await SeededAsync(fixture, new FakeTimeProvider(Start), counter);
        await using var _ = factory;
        var admin = factory.CreateClient().WithRole("Admin");
        var anonymous = factory.CreateClient().AsAnonymous();
        (await anonymous.GetAsync($"/api/posts/{post.Slug}/discussion")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await admin.PostAsync($"/api/posts/{post.Id}/unpublish", null)).EnsureSuccessStatusCode();
        (await anonymous.GetAsync($"/api/posts/{post.Slug}/discussion")).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "an unpublished post's discussion must 404 at once, not when the cache entry expires");

        (await admin.PostAsJsonAsync($"/api/posts/{post.Id}/publish", new PublishRequest())).EnsureSuccessStatusCode();
        (await anonymous.GetAsync($"/api/posts/{post.Slug}/discussion")).StatusCode.Should().Be(HttpStatusCode.OK,
            "and a publish clears the cached 404");

        (await admin.DeleteAsync($"/api/posts/{post.Id}")).EnsureSuccessStatusCode();
        (await anonymous.GetAsync($"/api/posts/{post.Slug}/discussion")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task TheSchedulerPublishingAPost_ClearsItsCachedNotFound()
    {
        var time = new FakeTimeProvider(Start);
        await using var factory = SocialApiFactory.Create(fixture, time: time);
        var admin = factory.CreateClient().WithRole("Admin");
        var anonymous = factory.CreateClient().AsAnonymous();
        var scheduled = await PostAsync(admin, $"Scheduled {Guid.NewGuid():N}", publishAt: Start.UtcDateTime.AddHours(2));
        (await anonymous.GetAsync($"/api/posts/{scheduled.Slug}/discussion")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        time.Advance(TimeSpan.FromHours(3));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var scheduler = new ScheduledPublisherHostedService(
                factory.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ScheduledPublisherHostedService>.Instance, time,
                factory.Services.GetRequiredService<IScheduledPublishWakeup>(), factory.Services.GetRequiredService<ICacheNotifier>());
            await scheduler.PublishDuePostsAsync(scope.ServiceProvider.GetRequiredService<BlogDbContext>());
        }

        (await anonymous.GetAsync($"/api/posts/{scheduled.Slug}/discussion")).StatusCode.Should().Be(HttpStatusCode.OK,
            "the scheduler's publish invalidates Discussion like any other publish");
    }
}
