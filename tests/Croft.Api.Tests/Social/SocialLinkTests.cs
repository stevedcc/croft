using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Croft.Api.Data;
using Croft.Api.Errors;
using Croft.Api.Models;
using Croft.Api.Social;
using Croft.Api.Tests.Fixtures;
using Croft.Api.Tests.Fixtures.Social;
using Croft.Shared.Contracts;
using Croft.Shared.Contracts.Social;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Api.Tests.Social;

/// <summary>
/// Brief 12a item 1: a post carries a Mastodon and a Bluesky post URL. They are validated on save (only the
/// owner's own posts may carry a discussion), a Bluesky handle is resolved once at save, and setting, changing or
/// clearing a link keeps the thread rows in step.
/// </summary>
public class SocialLinkTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private const string GoodMastodon = "https://mastodon.example/@owner/110054900866478823";
    private const string GoodBlueskyByDid = $"https://bsky.app/profile/{SocialTestSettings.Did}/post/3l6oveex3ii2l";

    [Theory]
    [InlineData("https://mastodon.example/@owner/110054900866478823", "110054900866478823")]
    [InlineData("https://MASTODON.example/@Owner/42", "42")]
    public async Task Mastodon_GoodUrl_IsSavedWithItsStatusId(string url, string statusId)
    {
        await using var factory = SocialApiFactory.Create(fixture);
        var admin = factory.CreateClient().WithRole("Admin");

        var created = await CreateAsync(admin, MastodonUrl: url);

        created.MastodonUrl.Should().Be(url);
        var stored = await LoadPostAsync(factory, created.Id);
        stored.MastodonStatusId.Should().Be(statusId, "the status id is the digits at the end of the URL");
        stored.DiscussionLinkedAt.Should().NotBeNull("a link was set");
    }

    [Theory]
    [InlineData("https://other.example/@owner/110054900866478823", "wrong host")]
    [InlineData("https://mastodon.example/@someone/110054900866478823", "wrong account")]
    [InlineData("https://mastodon.example/@owner/110054900866478823?x=1", "query string")]
    [InlineData("http://mastodon.example/@owner/110054900866478823", "http, not https")]
    [InlineData("https://mastodon.example/@owner/110054900866478823/", "trailing slash")]
    [InlineData("https://mastodon.example/@owner/110054900866478823#top", "fragment")]
    [InlineData("https://mastodon.example:8443/@owner/110054900866478823", "port")]
    [InlineData("https://mastodon.example/@owner/not-digits", "status id is not digits")]
    [InlineData("https://mastodon.example/@owner/", "no status id")]
    [InlineData("https://mastodon.example/@owner/110054900866478823/reblog", "extra path")]
    [InlineData(GoodBlueskyByDid, "a Bluesky URL in the Mastodon field")]
    [InlineData("not a url", "not a URL")]
    public async Task Mastodon_BadUrl_Is400NamingTheField_AndNothingIsSaved(string url, string why)
    {
        await using var factory = SocialApiFactory.Create(fixture);
        var admin = factory.CreateClient().WithRole("Admin");
        var title = $"Mastodon bad {Guid.NewGuid():N}";

        var response = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest(title, "body", MastodonUrl: url));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, why);
        (await DetailAsync(response)).Should().Contain("MastodonUrl", "the problem detail names the field");
        (await PostExistsAsync(factory, title)).Should().BeFalse("a refused post is not saved");
    }

    [Fact]
    public async Task Mastodon_NotConfigured_RefusesAnyUrl()
    {
        await using var factory = SocialApiFactory.Create(fixture, configuration: new() { ["Social:Mastodon:Server"] = "" });
        var admin = factory.CreateClient().WithRole("Admin");

        var response = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest($"Unconfigured {Guid.NewGuid():N}", "body", MastodonUrl: GoodMastodon));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "with no server configured no host can match");
        (await DetailAsync(response)).Should().Contain("MastodonUrl");
    }

    [Fact]
    public async Task Bluesky_DidUrl_IsSavedWithoutResolving_AndTheUriIsBuilt()
    {
        var resolver = new StubResolver();
        await using var factory = SocialApiFactory.Create(fixture, resolver);
        var admin = factory.CreateClient().WithRole("Admin");

        var created = await CreateAsync(admin, BlueskyUrl: GoodBlueskyByDid);

        created.BlueskyUrl.Should().Be(GoodBlueskyByDid);
        (await LoadPostAsync(factory, created.Id)).BlueskyUri.Should()
            .Be($"at://{SocialTestSettings.Did}/app.bsky.feed.post/3l6oveex3ii2l");
        resolver.Calls.Should().BeEmpty("a DID in the URL needs no resolution");
    }

    [Fact]
    public async Task Bluesky_HandleUrl_IsResolvedAtSave_ThroughTheStub()
    {
        var resolver = new StubResolver { Result = SocialTestSettings.Did };
        await using var factory = SocialApiFactory.Create(fixture, resolver);
        var admin = factory.CreateClient().WithRole("Admin");

        var created = await CreateAsync(admin, BlueskyUrl: "https://bsky.app/profile/Owner.Example/post/3l6oveex3ii2l");

        resolver.Calls.Should().Equal("owner.example");
        (await LoadPostAsync(factory, created.Id)).BlueskyUri.Should()
            .Be($"at://{SocialTestSettings.Did}/app.bsky.feed.post/3l6oveex3ii2l", "the handle was resolved to the owner's DID");
    }

    [Fact]
    public async Task Bluesky_HandleResolvingToAnotherDid_Is400()
    {
        var resolver = new StubResolver { Result = "did:plc:somebodyelse0000000000000" };
        await using var factory = SocialApiFactory.Create(fixture, resolver);
        var admin = factory.CreateClient().WithRole("Admin");
        var title = $"Other did {Guid.NewGuid():N}";

        var response = await admin.PostAsJsonAsync("/api/posts",
            new CreatePostRequest(title, "body", BlueskyUrl: "https://bsky.app/profile/someone.example/post/3l6oveex3ii2l"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await DetailAsync(response)).Should().Contain("BlueskyUrl");
        (await PostExistsAsync(factory, title)).Should().BeFalse();
    }

    [Fact]
    public async Task Bluesky_HandleThatDoesNotResolve_Is400()
    {
        var resolver = new StubResolver { Failure = new ValidationException("The handle does not resolve.") };
        await using var factory = SocialApiFactory.Create(fixture, resolver);
        var admin = factory.CreateClient().WithRole("Admin");

        var response = await admin.PostAsJsonAsync("/api/posts",
            new CreatePostRequest($"Unknown handle {Guid.NewGuid():N}", "body", BlueskyUrl: "https://bsky.app/profile/nobody.example/post/3l6oveex3ii2l"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Bluesky_ResolutionFailure_Is503WithAProblemDetail_AndNothingIsSaved()
    {
        var resolver = new StubResolver { Failure = new UpstreamUnavailableException("Bluesky did not answer.") };
        await using var factory = SocialApiFactory.Create(fixture, resolver);
        var admin = factory.CreateClient().WithRole("Admin");
        var title = $"Resolution down {Guid.NewGuid():N}";

        var response = await admin.PostAsJsonAsync("/api/posts",
            new CreatePostRequest(title, "body", BlueskyUrl: "https://bsky.app/profile/owner.example/post/3l6oveex3ii2l"));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await DetailAsync(response)).Should().Contain("Bluesky");
        (await PostExistsAsync(factory, title)).Should().BeFalse("a failed resolution is not a silent save");
    }

    [Theory]
    [InlineData("https://bsky.app/profile/did:plc:somebodyelse0000000000000/post/3l6oveex3ii2l", "another DID")]
    [InlineData($"http://bsky.app/profile/{SocialTestSettings.Did}/post/3l6oveex3ii2l", "http")]
    [InlineData($"https://bsky.app/profile/{SocialTestSettings.Did}/post/3l6oveex3ii2l?x=1", "query string")]
    [InlineData($"https://bsky.app/profile/{SocialTestSettings.Did}/post/3l6oveex3ii2l/", "trailing slash")]
    [InlineData($"https://bsky.app/profile/{SocialTestSettings.Did}/post/3l6oveex3ii2l/extra", "extra path")]
    [InlineData($"https://example.com/profile/{SocialTestSettings.Did}/post/3l6oveex3ii2l", "wrong host")]
    [InlineData($"https://bsky.app/profile/{SocialTestSettings.Did}", "a profile, not a post")]
    [InlineData(GoodMastodon, "a Mastodon URL in the Bluesky field")]
    public async Task Bluesky_BadUrl_Is400NamingTheField_AndNothingIsSaved(string url, string why)
    {
        await using var factory = SocialApiFactory.Create(fixture, new StubResolver());
        var admin = factory.CreateClient().WithRole("Admin");
        var title = $"Bluesky bad {Guid.NewGuid():N}";

        var response = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest(title, "body", BlueskyUrl: url));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, why);
        (await DetailAsync(response)).Should().Contain("BlueskyUrl");
        (await PostExistsAsync(factory, title)).Should().BeFalse();
    }

    [Fact]
    public async Task WithSocialDisabled_TheFieldsStillValidateAndSave()
    {
        await using var factory = SocialApiFactory.Create(fixture, new StubResolver(), enabled: false);
        var admin = factory.CreateClient().WithRole("Admin");

        var created = await CreateAsync(admin, MastodonUrl: GoodMastodon, BlueskyUrl: GoodBlueskyByDid);
        var refused = await admin.PostAsJsonAsync("/api/posts",
            new CreatePostRequest($"Disabled bad {Guid.NewGuid():N}", "body", MastodonUrl: "https://other.example/@owner/1"));

        created.MastodonUrl.Should().Be(GoodMastodon, "the owner can prepare posts while fetching is off");
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, "and a bad link is refused just the same");
    }

    [Fact]
    public async Task SettingALink_StartsTheSchedulesClock_AndMakesTheThreadDueNow()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero));
        await using var factory = SocialApiFactory.Create(fixture, new StubResolver(), time: time);
        var admin = factory.CreateClient().WithRole("Admin");

        var created = await CreateAsync(admin, MastodonUrl: GoodMastodon);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        var post = await db.Posts.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        post.DiscussionLinkedAt.Should().Be(time.GetUtcNow().UtcDateTime);
        var thread = await db.SocialThreads.AsNoTracking().SingleAsync(t => t.PostId == created.Id);
        thread.Platform.Should().Be(SocialPlatform.Mastodon);
        thread.NextDueAt.Should().Be(time.GetUtcNow().UtcDateTime, "a new link makes its thread due now");
        (await db.SocialThreads.AsNoTracking().CountAsync(t => t.PostId == created.Id)).Should().Be(1, "only the linked platform has a thread");
    }

    [Fact]
    public async Task ChangingALink_DeletesTheOldRepliesAndResetsTheThread()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero));
        await using var factory = SocialApiFactory.Create(fixture, new StubResolver(), time: time);
        var admin = factory.CreateClient().WithRole("Admin");
        var created = await CreateAsync(admin, MastodonUrl: GoodMastodon);
        await SeedThreadStateAsync(factory, created.Id, SocialPlatform.Mastodon, "old-reply-1");
        time.Advance(TimeSpan.FromHours(3));

        var update = await admin.PutAsJsonAsync($"/api/posts/{created.Id}",
            new UpdatePostRequest(created.Title, "body", MastodonUrl: "https://mastodon.example/@owner/2222"));

        update.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        (await db.SocialReplies.AsNoTracking().CountAsync(r => r.PostId == created.Id)).Should().Be(0, "replies of the old post are not replies of the new one");
        var thread = await db.SocialThreads.AsNoTracking().SingleAsync(t => t.PostId == created.Id);
        thread.SnapshotHash.Should().BeNull();
        thread.LastSuccessAt.Should().BeNull();
        thread.NextDueAt.Should().Be(time.GetUtcNow().UtcDateTime, "the changed link is fetched at once");
        (await db.Posts.AsNoTracking().SingleAsync(p => p.Id == created.Id)).DiscussionLinkedAt.Should().Be(time.GetUtcNow().UtcDateTime);
    }

    [Fact]
    public async Task SavingTheSameLinkAgain_ChangesNothing()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero));
        await using var factory = SocialApiFactory.Create(fixture, new StubResolver(), time: time);
        var admin = factory.CreateClient().WithRole("Admin");
        var created = await CreateAsync(admin, MastodonUrl: GoodMastodon);
        await SeedThreadStateAsync(factory, created.Id, SocialPlatform.Mastodon, "kept-reply");
        time.Advance(TimeSpan.FromHours(3));

        var update = await admin.PutAsJsonAsync($"/api/posts/{created.Id}",
            new UpdatePostRequest(created.Title, "edited body", MastodonUrl: GoodMastodon));

        update.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        (await db.SocialReplies.AsNoTracking().CountAsync(r => r.PostId == created.Id)).Should().Be(1, "an unchanged link keeps its replies");
        var thread = await db.SocialThreads.AsNoTracking().SingleAsync(t => t.PostId == created.Id);
        thread.LastSuccessAt.Should().NotBeNull("an unchanged link keeps its thread state");
        (await db.Posts.AsNoTracking().SingleAsync(p => p.Id == created.Id)).DiscussionLinkedAt.Should()
            .Be(new DateTime(2026, 10, 21, 9, 0, 0, DateTimeKind.Utc), "the schedule's clock only moves when a link changes");
    }

    [Fact]
    public async Task ClearingALink_DeletesThatPlatformsThreadAndReplies_AndKeepsTheOther()
    {
        await using var factory = SocialApiFactory.Create(fixture, new StubResolver());
        var admin = factory.CreateClient().WithRole("Admin");
        var created = await CreateAsync(admin, MastodonUrl: GoodMastodon, BlueskyUrl: GoodBlueskyByDid);
        await SeedThreadStateAsync(factory, created.Id, SocialPlatform.Mastodon, "mastodon-reply");
        await SeedThreadStateAsync(factory, created.Id, SocialPlatform.Bluesky, "at://did:plc:x/app.bsky.feed.post/bluesky-reply");

        var update = await admin.PutAsJsonAsync($"/api/posts/{created.Id}",
            new UpdatePostRequest(created.Title, "body", MastodonUrl: "", BlueskyUrl: GoodBlueskyByDid));

        update.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await update.Content.ReadFromJsonAsync<PostDto>())!;
        dto.MastodonUrl.Should().BeNull();
        dto.BlueskyUrl.Should().Be(GoodBlueskyByDid);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        var post = await db.Posts.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        post.MastodonStatusId.Should().BeNull("clearing the URL clears its stored id");
        post.BlueskyUri.Should().NotBeNull();
        (await db.SocialThreads.AsNoTracking().Where(t => t.PostId == created.Id).Select(t => t.Platform).ToListAsync())
            .Should().Equal(SocialPlatform.Bluesky);
        (await db.SocialReplies.AsNoTracking().Where(r => r.PostId == created.Id).Select(r => r.Platform).ToListAsync())
            .Should().Equal(SocialPlatform.Bluesky);
    }

    [Fact]
    public async Task AWhitespaceOnlyUrl_ClearsTheLinkToo()
    {
        await using var factory = SocialApiFactory.Create(fixture, new StubResolver());
        var admin = factory.CreateClient().WithRole("Admin");
        var created = await CreateAsync(admin, MastodonUrl: GoodMastodon);
        await SeedThreadStateAsync(factory, created.Id, SocialPlatform.Mastodon, "mastodon-reply");

        var update = await admin.PutAsJsonAsync($"/api/posts/{created.Id}", new UpdatePostRequest(created.Title, "body", MastodonUrl: "   "));

        update.StatusCode.Should().Be(HttpStatusCode.OK);
        (await update.Content.ReadFromJsonAsync<PostDto>())!.MastodonUrl.Should().BeNull();
        await AssertLinksThreadsAndRepliesAsync(factory, created.Id, expectMastodon: false, expectBluesky: false);
    }

    [Fact]
    public async Task AnUpdateWithNullLinks_LeavesLinksThreadsAndRepliesUnchanged()
    {
        await using var factory = SocialApiFactory.Create(fixture, new StubResolver());
        var admin = factory.CreateClient().WithRole("Admin");
        var created = await CreateAsync(admin, MastodonUrl: GoodMastodon, BlueskyUrl: GoodBlueskyByDid);
        await SeedThreadStateAsync(factory, created.Id, SocialPlatform.Mastodon, "mastodon-reply");
        await SeedThreadStateAsync(factory, created.Id, SocialPlatform.Bluesky, "at://did:plc:x/app.bsky.feed.post/bluesky-reply");

        var update = await admin.PutAsJsonAsync($"/api/posts/{created.Id}", new UpdatePostRequest(created.Title, "edited body"));

        update.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await update.Content.ReadFromJsonAsync<PostDto>())!;
        (dto.MastodonUrl, dto.BlueskyUrl).Should().Be((GoodMastodon, GoodBlueskyByDid));
        await AssertLinksThreadsAndRepliesAsync(factory, created.Id, expectMastodon: true, expectBluesky: true);
    }

    [Fact]
    public async Task AnUpdateBodyThatOmitsTheLinkMembers_LeavesEverythingUnchanged_TheOldWebPodCase()
    {
        await using var factory = SocialApiFactory.Create(fixture, new StubResolver());
        var admin = factory.CreateClient().WithRole("Admin");
        var created = await CreateAsync(admin, MastodonUrl: GoodMastodon, BlueskyUrl: GoodBlueskyByDid);
        await SeedThreadStateAsync(factory, created.Id, SocialPlatform.Mastodon, "mastodon-reply");
        await SeedThreadStateAsync(factory, created.Id, SocialPlatform.Bluesky, "at://did:plc:x/app.bsky.feed.post/bluesky-reply");
        var body = new StringContent($$"""{"title":"{{created.Title}}","markdownContent":"edited by an older pod","kind":0,"navigationPlacement":0,"navigationOrder":0}""",
            System.Text.Encoding.UTF8, "application/json");

        var update = await admin.PutAsync($"/api/posts/{created.Id}", body);

        update.StatusCode.Should().Be(HttpStatusCode.OK, await update.Content.ReadAsStringAsync());
        await AssertLinksThreadsAndRepliesAsync(factory, created.Id, expectMastodon: true, expectBluesky: true);
    }

    private static async Task AssertLinksThreadsAndRepliesAsync(BlogApiFactory factory, Guid postId, bool expectMastodon, bool expectBluesky)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        var post = await db.Posts.AsNoTracking().SingleAsync(p => p.Id == postId);
        (post.MastodonUrl is not null).Should().Be(expectMastodon, "the Mastodon link");
        (post.BlueskyUrl is not null).Should().Be(expectBluesky, "the Bluesky link");
        var wanted = new List<SocialPlatform>();
        if (expectMastodon) wanted.Add(SocialPlatform.Mastodon);
        if (expectBluesky) wanted.Add(SocialPlatform.Bluesky);
        (await db.SocialThreads.AsNoTracking().Where(t => t.PostId == postId).OrderBy(t => t.Platform).Select(t => t.Platform).ToListAsync())
            .Should().Equal(wanted, "the thread rows");
        (await db.SocialReplies.AsNoTracking().Where(r => r.PostId == postId).OrderBy(r => r.Platform).Select(r => r.Platform).ToListAsync())
            .Should().Equal(wanted, "the stored replies");
    }

    [Fact]
    public async Task PostDto_RoundTripsTheUrls_OnStaffAndPublicReads()
    {
        await using var factory = SocialApiFactory.Create(fixture, new StubResolver());
        var admin = factory.CreateClient().WithRole("Admin");
        var created = await CreateAsync(admin, MastodonUrl: GoodMastodon, BlueskyUrl: GoodBlueskyByDid);
        (await admin.PostAsJsonAsync($"/api/posts/{created.Id}/publish", new PublishRequest())).EnsureSuccessStatusCode();

        var staff = (await admin.GetFromJsonAsync<PostDto>($"/api/posts/{created.Slug}"))!;
        var publicRead = (await factory.CreateClient().AsAnonymous().GetFromJsonAsync<PostDto>($"/api/posts/{created.Slug}"))!;

        staff.MastodonUrl.Should().Be(GoodMastodon);
        staff.BlueskyUrl.Should().Be(GoodBlueskyByDid);
        publicRead.MastodonUrl.Should().Be(GoodMastodon);
        publicRead.BlueskyUrl.Should().Be(GoodBlueskyByDid);
    }

    private static async Task<PostDto> CreateAsync(HttpClient admin, string? MastodonUrl = null, string? BlueskyUrl = null)
    {
        var response = await admin.PostAsJsonAsync("/api/posts",
            new CreatePostRequest($"Linked post {Guid.NewGuid():N}", "body", MastodonUrl: MastodonUrl, BlueskyUrl: BlueskyUrl));
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PostDto>())!;
    }

    private static async Task<string> DetailAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("detail", out var detail) ? detail.GetString() ?? "" : "";
    }

    private static async Task<Post> LoadPostAsync(BlogApiFactory factory, Guid id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<BlogDbContext>().Posts.AsNoTracking().SingleAsync(p => p.Id == id);
    }

    private static async Task<bool> PostExistsAsync(BlogApiFactory factory, string title)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<BlogDbContext>().Posts.AnyAsync(p => p.Title == title);
    }

    /// <summary>Gives the platform's thread a successful fetch and one stored reply, as a refresh would have left them.</summary>
    private static async Task SeedThreadStateAsync(BlogApiFactory factory, Guid postId, SocialPlatform platform, string replyId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        var thread = await db.SocialThreads.SingleAsync(t => t.PostId == postId && t.Platform == platform);
        thread.LastSuccessAt = DateTime.UtcNow;
        thread.SnapshotHash = "seeded-hash";
        thread.NextDueAt = DateTime.UtcNow.AddHours(1);
        db.SocialReplies.Add(new SocialReply
        {
            PostId = postId,
            Platform = platform,
            ExternalId = replyId,
            AuthorHandle = "reader",
            AuthorDisplayName = "Reader",
            AuthorUrl = "https://social.example/@reader",
            AuthorAccountId = "1",
            CreatedAt = DateTime.UtcNow,
            Permalink = "https://social.example/@reader/1",
            ContentHtml = "<p>Example reply text.</p>",
            FetchedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private sealed class StubResolver : IBlueskyHandleResolver
    {
        public List<string> Calls { get; } = [];

        public string Result { get; init; } = SocialTestSettings.Did;

        public Exception? Failure { get; init; }

        public Task<string> ResolveDidAsync(string handle, CancellationToken ct = default)
        {
            Calls.Add(handle);
            return Failure is null ? Task.FromResult(Result) : Task.FromException<string>(Failure);
        }
    }
}
