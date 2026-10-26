using AwesomeAssertions;
using Croft.Api.Services;
using Croft.Api.Tests.Fixtures;
using Croft.Api.Tests.Fixtures.Social;
using Croft.Shared.Contracts;
using Croft.Shared.Contracts.Social;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Croft.Api.Tests.Fixtures.Social.DiscussionSeed;

namespace Croft.Api.Tests.Social;

/// <summary>
/// Brief 17e item 4: the posts list, the post read, related posts and search carry the announcements' counts (both networks summed), the
/// discussion read carries them per network; the counts are attached after the posts cache and cached under Discussion.
/// </summary>
public class SocialCountsReadTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private static readonly SocialCountsDto Mastodon = new(5, 2, 1);
    private static readonly SocialCountsDto Bluesky = new(7, 3, 4);
    private static readonly SocialCountsDto Sum = new(12, 5, 5);

    private static async Task<PostSummaryDto> ListedAsync(HttpClient client, Guid id)
    {
        var page = (await client.GetFromJsonAsync<PagedResult<PostSummaryDto>>("/api/posts?publicView=true&pageSize=50"))!;
        return page.Items.Single(p => p.Id == id);
    }

    [Fact]
    public async Task APostWithCountsOnBothPlatforms_ListsTheirSum_OnEveryPublicRead()
    {
        await using var factory = SocialApiFactory.Create(fixture);
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await PostAsync(admin, "Zorblat counted post");
        var other = await PostAsync(admin, "Zorblat neighbour", mastodon: false, bluesky: false);
        await SetCountsAsync(factory, post.Id, SocialPlatform.Mastodon, Mastodon.Likes, Mastodon.Reposts, Mastodon.Replies);
        await SetCountsAsync(factory, post.Id, SocialPlatform.Bluesky, Bluesky.Likes, Bluesky.Reposts, Bluesky.Replies);
        var anonymous = factory.CreateClient().AsAnonymous();

        (await ListedAsync(anonymous, post.Id)).Social.Should().Be(Sum, "the list sums the two networks");
        (await ListedAsync(anonymous, other.Id)).Social.Should().BeNull("a post with no threads has no counts");
        (await anonymous.GetFromJsonAsync<PostDto>($"/api/posts/{post.Slug}"))!.Social.Should().Be(Sum, "the post read");
        var related = (await anonymous.GetFromJsonAsync<List<PostSummaryDto>>($"/api/posts/{other.Slug}/related"))!;
        related.Single(p => p.Id == post.Id).Social.Should().Be(Sum, "related posts");
        var found = (await anonymous.GetFromJsonAsync<PagedResult<PostSummaryDto>>("/api/search?q=zorblat"))!;
        found.Items.Single(p => p.Id == post.Id).Social.Should().Be(Sum, "search");
    }

    [Fact]
    public async Task CountsOnOnePlatformOnly_AreThatPlatformsNumbers()
    {
        await using var factory = SocialApiFactory.Create(fixture);
        var post = await PostAsync(factory.CreateClient().WithRole("Admin"), "One platform counted");
        await SetCountsAsync(factory, post.Id, SocialPlatform.Bluesky, Bluesky.Likes, Bluesky.Reposts, Bluesky.Replies);

        (await ListedAsync(factory.CreateClient().AsAnonymous(), post.Id)).Social.Should().Be(Bluesky);
    }

    [Fact]
    public async Task LinkedThreadsThatNeverReturnedCounts_HaveNoSocial()
    {
        await using var factory = SocialApiFactory.Create(fixture);
        var post = await PostAsync(factory.CreateClient().WithRole("Admin"), "Linked but not yet counted");

        (await ListedAsync(factory.CreateClient().AsAnonymous(), post.Id)).Social.Should().BeNull();
    }

    [Fact]
    public async Task WithSocialDisabled_NoCountsAreAttached()
    {
        await using var factory = SocialApiFactory.Create(fixture, enabled: false);
        var post = await PostAsync(factory.CreateClient().WithRole("Admin"), "Disabled counted");
        await SetCountsAsync(factory, post.Id, SocialPlatform.Mastodon, 1, 1, 1);

        (await ListedAsync(factory.CreateClient().AsAnonymous(), post.Id)).Social.Should().BeNull();
    }

    [Fact]
    public async Task ACountChange_ReachesTheList_WithoutAPostWrite_AndWithoutFlushingThePostsCache()
    {
        await using var factory = SocialApiFactory.Create(fixture);
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await PostAsync(admin, "Count changes underneath");
        await SetCountsAsync(factory, post.Id, SocialPlatform.Mastodon, Mastodon.Likes, Mastodon.Reposts, Mastodon.Replies);
        var anonymous = factory.CreateClient().AsAnonymous();
        (await ListedAsync(anonymous, post.Id)).Social.Should().Be(Mastodon);

        // The way the fetcher does it: new counts in the row, then the Discussion group (only) invalidated. A title changed straight in
        // the database (no invalidation) stays hidden by the Posts entry: that entry was not evicted.
        await SetCountsAsync(factory, post.Id, SocialPlatform.Mastodon, Mastodon.Likes + 1, Mastodon.Reposts, Mastodon.Replies);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Croft.Api.Data.BlogDbContext>();
            await db.Posts.Where(p => p.Id == post.Id).ExecuteUpdateAsync(set => set.SetProperty(p => p.Title, "Changed behind the cache"));
            await scope.ServiceProvider.GetRequiredService<ICacheNotifier>().InvalidateLocalAsync(CacheGroup.Discussion);
        }

        var after = await ListedAsync(anonymous, post.Id);
        after.Social.Should().Be(new SocialCountsDto(6, 2, 1), "the new like count, with no post write");
        after.Title.Should().Be("Count changes underneath", "the Posts cache entry was not evicted by the Discussion invalidation");
    }

    [Fact]
    public async Task TheDiscussionRead_CarriesTheCountsPerPlatform_AndNullWhereUnknown()
    {
        await using var factory = SocialApiFactory.Create(fixture);
        var post = await PostAsync(factory.CreateClient().WithRole("Admin"), "Discussion with counts");
        await SetCountsAsync(factory, post.Id, SocialPlatform.Mastodon, Mastodon.Likes, Mastodon.Reposts, Mastodon.Replies);
        var anonymous = factory.CreateClient().AsAnonymous();

        var discussion = await ReadAsync(anonymous, post.Slug);

        discussion.MastodonCounts.Should().Be(Mastodon);
        discussion.BlueskyCounts.Should().BeNull("that thread has no counts yet");
    }
}
