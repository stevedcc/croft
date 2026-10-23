using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Croft.Api.Tests.Fixtures;
using Croft.Api.Tests.Fixtures.Social;
using Croft.Shared.Contracts.Social;
using Microsoft.Extensions.DependencyInjection;
using static Croft.Api.Tests.Fixtures.Social.DiscussionSeed;

namespace Croft.Api.Tests.Social;

/// <summary>Brief 12a item 5: <c>GET /api/posts/{slug}/discussion</c> and the staff read.</summary>
public class DiscussionReadTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private static DateTime At(int minutes) => Noon.AddMinutes(minutes);

    [Fact]
    public async Task BothPlatformsAreMergedByTime_WithTheirAnswersNested()
    {
        await using var factory = SocialApiFactory.Create(fixture, time: new FakeTimeProvider(new DateTimeOffset(Noon.AddHours(1))));
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await PostAsync(admin, "Merged discussion");
        await StoreAsync(factory, post.Id, SocialPlatform.Mastodon, Noon,
            new ReplySpec("m1", null, At(0)), new ReplySpec("m2", null, At(20)), new ReplySpec("m2a", "m2", At(25)));
        await StoreAsync(factory, post.Id, SocialPlatform.Bluesky, Noon,
            new ReplySpec("b1", null, At(10)), new ReplySpec("b1a", "b1", At(12)));

        var discussion = await ReadAsync(factory.CreateClient().AsAnonymous(), post.Slug);

        Shape(discussion.Replies).Should().Equal("m1", "b1[b1a]", "m2[m2a]");
        discussion.ReplyCount.Should().Be(5, "every reply in the tree, nested ones included");
        discussion.MastodonUrl.Should().Be(MastodonLink);
        discussion.BlueskyUrl.Should().Be(BlueskyLink);
        discussion.Replies[1].Platform.Should().Be(SocialPlatform.Bluesky);
        discussion.Replies[0].ContentHtml.Should().Be("<p>Example reply text m1.</p>");
    }

    [Fact]
    public async Task APostWithNoLinks_HasAnEmptyDiscussion()
    {
        await using var factory = SocialApiFactory.Create(fixture);
        var post = await PostAsync(factory.CreateClient().WithRole("Admin"), "No links", mastodon: false, bluesky: false);

        var discussion = await ReadAsync(factory.CreateClient().AsAnonymous(), post.Slug);

        discussion.Should().BeEquivalentTo(new PostDiscussionDto(null, null, 0, []));
    }

    [Fact]
    public async Task OneLinkedPlatform_ReturnsOnlyThatLink()
    {
        await using var factory = SocialApiFactory.Create(fixture);
        var post = await PostAsync(factory.CreateClient().WithRole("Admin"), "One link", bluesky: false);

        var discussion = await ReadAsync(factory.CreateClient().AsAnonymous(), post.Slug);

        discussion.MastodonUrl.Should().Be(MastodonLink);
        discussion.BlueskyUrl.Should().BeNull();
    }

    [Fact]
    public async Task ADraft_AScheduledPost_AndAnUnknownSlug_Are404()
    {
        await using var factory = SocialApiFactory.Create(fixture);
        var admin = factory.CreateClient().WithRole("Admin");
        var draft = await PostAsync(admin, "A draft with links", publish: false);
        var scheduled = await PostAsync(admin, "A scheduled post with links", publishAt: DateTime.UtcNow.AddDays(3));
        var anonymous = factory.CreateClient().AsAnonymous();

        (await anonymous.GetAsync($"/api/posts/{draft.Slug}/discussion")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await anonymous.GetAsync($"/api/posts/{scheduled.Slug}/discussion")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await anonymous.GetAsync("/api/posts/no-such-post/discussion")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.GetAsync($"/api/posts/{draft.Slug}/discussion")).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the public read is the public read, whoever asks: a draft has no public discussion");
    }

    [Fact]
    public async Task WithSocialDisabled_NoLinksAndNoReplies_AreReturned()
    {
        await using var factory = SocialApiFactory.Create(fixture, enabled: false, time: new FakeTimeProvider(new DateTimeOffset(Noon.AddHours(1))));
        var post = await PostAsync(factory.CreateClient().WithRole("Admin"), "Disabled discussion");
        await StoreAsync(factory, post.Id, SocialPlatform.Mastodon, Noon, new ReplySpec("m1", null, At(0)));

        var discussion = await ReadAsync(factory.CreateClient().AsAnonymous(), post.Slug);

        discussion.Should().BeEquivalentTo(new PostDiscussionDto(null, null, 0, []), "with Social:Enabled=false the read returns no section");
    }

    [Fact]
    public async Task AThreadWhoseLastSuccessIsOlderThan48Hours_ContributesNoReplies_ButTheOtherDoes()
    {
        await using var factory = SocialApiFactory.Create(fixture, time: new FakeTimeProvider(new DateTimeOffset(Noon.AddHours(60))));
        var post = await PostAsync(factory.CreateClient().WithRole("Admin"), "Partly stale");
        await StoreAsync(factory, post.Id, SocialPlatform.Mastodon, Noon.AddHours(5), new ReplySpec("m1", null, At(0)));   // 55 h old
        await StoreAsync(factory, post.Id, SocialPlatform.Bluesky, Noon.AddHours(40), new ReplySpec("b1", null, At(10)));   // 20 h old

        var discussion = await ReadAsync(factory.CreateClient().AsAnonymous(), post.Slug);

        Shape(discussion.Replies).Should().Equal("b1");
        discussion.ReplyCount.Should().Be(1);
        discussion.MastodonUrl.Should().Be(MastodonLink, "the reply actions stay even while the replies are hidden");
    }

    [Fact]
    public async Task TheStaffRead_ShowsEveryStoredReply_AndEachThreadsStatus()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(Noon.AddHours(60)));
        await using var factory = SocialApiFactory.Create(fixture, time: time);
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await PostAsync(admin, "Staff view");
        await StoreAsync(factory, post.Id, SocialPlatform.Mastodon, Noon.AddHours(5), new ReplySpec("m1", null, At(0)), new ReplySpec("m1a", "m1", At(3)));
        await StoreAsync(factory, post.Id, SocialPlatform.Bluesky, Noon.AddHours(40), new ReplySpec("b1", null, At(10)));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Croft.Api.Data.BlogDbContext>();
            var thread = db.SocialThreads.Single(t => t.PostId == post.Id && t.Platform == SocialPlatform.Bluesky);
            thread.LastError = "Bluesky answered HTTP 502";
            thread.LastAttemptAt = Noon.AddHours(50);
            await db.SaveChangesAsync();
        }

        var response = await admin.GetAsync($"/api/posts/{post.Id}/discussion/admin");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = (await response.Content.ReadFromJsonAsync<PostDiscussionAdminDto>())!;
        Shape(result.Discussion.Replies).Should().Equal("m1[m1a]", "b1");
        result.Discussion.ReplyCount.Should().Be(3, "a stale thread's replies are shown to staff too");
        var mastodon = result.Threads.Single(t => t.Platform == SocialPlatform.Mastodon);
        mastodon.StoredReplies.Should().Be(2);
        mastodon.Stale.Should().BeTrue("its last success is 55 hours old");
        mastodon.LastSuccessAt.Should().Be(new DateTimeOffset(Noon.AddHours(5)));
        var bluesky = result.Threads.Single(t => t.Platform == SocialPlatform.Bluesky);
        bluesky.Stale.Should().BeFalse();
        bluesky.LastError.Should().Be("Bluesky answered HTTP 502");
        bluesky.LastAttemptAt.Should().Be(new DateTimeOffset(Noon.AddHours(50)));
        bluesky.NextDueAt.Should().NotBeNull();
    }

    [Fact]
    public async Task TheStaffRead_OfAPostWithNoLinks_HasNoThreads_AndOfAnUnknownPost_Is404()
    {
        await using var factory = SocialApiFactory.Create(fixture);
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await PostAsync(admin, "Unlinked", mastodon: false, bluesky: false, publish: false);

        var result = (await admin.GetFromJsonAsync<PostDiscussionAdminDto>($"/api/posts/{post.Id}/discussion/admin"))!;

        result.Threads.Should().BeEmpty();
        (await admin.GetAsync($"/api/posts/{Guid.NewGuid()}/discussion/admin")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Refresh_MarksBothThreadsDueNow_With202_AndAnUnknownPostIs404()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(Noon));
        await using var factory = SocialApiFactory.Create(fixture, time: time);
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await PostAsync(admin, "Refresh me");
        await StoreAsync(factory, post.Id, SocialPlatform.Mastodon, Noon.AddHours(-3));
        await StoreAsync(factory, post.Id, SocialPlatform.Bluesky, Noon.AddHours(-3));
        time.Advance(TimeSpan.FromMinutes(1));

        var response = await admin.PostAsync($"/api/posts/{post.Id}/discussion/refresh", null);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var threads = (await admin.GetFromJsonAsync<PostDiscussionAdminDto>($"/api/posts/{post.Id}/discussion/admin"))!.Threads;
        threads.Should().HaveCount(2).And.OnlyContain(t => t.NextDueAt == new DateTimeOffset(Noon.AddMinutes(1)));
        (await admin.PostAsync($"/api/posts/{Guid.NewGuid()}/discussion/refresh", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
