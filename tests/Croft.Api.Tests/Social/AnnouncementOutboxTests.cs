using System.Net;
using AwesomeAssertions;
using Croft.Api.Data;
using Croft.Api.Models;
using Croft.Api.Services;
using Croft.Api.Social;
using Croft.Api.Tests.Fixtures;
using Croft.Api.Tests.Fixtures.Social;
using Croft.Shared.Contracts;
using Croft.Shared.Contracts.Social;
using Croft.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Croft.Api.Tests.Social;

/// <summary>
/// Brief 12d item 2: a post is queued for announcing on the transition to Published only (by the button or by the scheduler, in the same
/// transaction as the status change), unpublishing cancels what is pending, and the two endpoints read the rows and queue "Announce now" / "Retry".
/// </summary>
public class AnnouncementOutboxTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private const string GoodMastodon = "https://mastodon.example/@owner/110054900866478823";

    private static Dictionary<string, string?> Ready(bool mastodon = true, bool bluesky = true) => new()
    {
        ["Blog:BaseUrl"] = "https://blog.example",
        ["Social:Mastodon:AccessToken"] = mastodon ? "test-token-0f3a" : "",
        ["Social:Bluesky:AppPassword"] = bluesky ? "test-pass-77c1" : "",
    };

    private BlogApiFactory Factory(bool mastodon = true, bool bluesky = true, TimeProvider? time = null) =>
        SocialApiFactory.Create(fixture, configuration: Ready(mastodon, bluesky), time: time, configureServices: services =>
        {
            // The tests run the scheduler's tick themselves: the host's own loop would hold the job lock now and then and make a tick skip.
            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) && d.ImplementationType == typeof(ScheduledPublisherHostedService)).ToList())
                services.Remove(descriptor);
        });

    [Fact]
    public async Task PublishingADraft_QueuesOnePendingRowPerReadyNetwork()
    {
        await using var factory = Factory();
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreateAsync(admin, "Queue both");

        await PublishAsync(admin, post);

        var rows = await RowsAsync(factory, post.Id);
        rows.Select(r => (r.Platform, r.State)).Should().BeEquivalentTo([(SocialPlatform.Mastodon, AnnouncementState.Pending), (SocialPlatform.Bluesky, AnnouncementState.Pending)]);
        rows.Should().OnlyContain(r => r.Attempts == 0 && r.NextAttemptAt != null && r.QueuedAt != default && r.LeaseUntil == null);
    }

    [Fact]
    public async Task ANetworkThatIsNotReady_GetsNoRow()
    {
        await using var factory = Factory(bluesky: false);
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreateAsync(admin, "Queue one ready");

        await PublishAsync(admin, post);

        (await RowsAsync(factory, post.Id)).Select(r => r.Platform).Should().Equal(SocialPlatform.Mastodon);
    }

    [Fact]
    public async Task PublishingAPublishedPost_QueuesNothingAndChangesNothing()
    {
        await using var factory = Factory();
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreateAsync(admin, "Queue once");
        await PublishAsync(admin, post);
        await MutateRowsAsync(factory, post.Id, r => { r.LastError = "marker"; r.QueuedAt = r.QueuedAt.AddHours(-1); });
        var before = await RowsAsync(factory, post.Id);

        await PublishAsync(admin, post);

        var after = await RowsAsync(factory, post.Id);
        after.Should().HaveCount(2);
        after.Select(r => (r.Platform, r.State, r.QueuedAt, r.LastError)).Should().BeEquivalentTo(before.Select(r => (r.Platform, r.State, r.QueuedAt, r.LastError)));
    }

    [Fact]
    public async Task PublishingAPublishedPost_QueuesNothing_EvenForANetworkThatHasNoRow()
    {
        await using var factory = Factory();
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreateAsync(admin, "Queue once more", new CreatePostRequest("Queue once more", "body", AnnounceMastodon: false, AnnounceBluesky: false));
        await PublishAsync(admin, post);
        (await admin.PutAsJsonAsync($"/api/posts/{post.Id}", new UpdatePostRequest(post.Title, "body", AnnounceMastodon: true, AnnounceBluesky: true))).EnsureSuccessStatusCode();

        await PublishAsync(admin, post);

        (await RowsAsync(factory, post.Id)).Should().BeEmpty("only the transition to Published queues; a flag switched on later is what \"Announce now\" is for");
    }

    [Fact]
    public async Task AFlagThatIsOff_QueuesNothingForThatNetwork()
    {
        await using var factory = Factory();
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreateAsync(admin, "Queue flag off", new CreatePostRequest("Queue flag off", "body", AnnounceMastodon: false));

        await PublishAsync(admin, post);

        (await RowsAsync(factory, post.Id)).Select(r => r.Platform).Should().Equal(SocialPlatform.Bluesky);
    }

    [Fact]
    public async Task AHandLinkedNetwork_QueuesNothingForThatNetwork()
    {
        await using var factory = Factory();
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreateAsync(admin, "Queue hand linked", new CreatePostRequest("Queue hand linked", "body", MastodonUrl: GoodMastodon));

        await PublishAsync(admin, post);

        (await RowsAsync(factory, post.Id)).Select(r => r.Platform).Should().Equal(SocialPlatform.Bluesky);
    }

    [Fact]
    public async Task APage_IsNeverQueued_EvenWithTheFlagsForcedOn()
    {
        await using var factory = Factory();
        var admin = factory.CreateClient().WithRole("Admin");
        var page = await CreateAsync(admin, "Queue page", new CreatePostRequest("Queue page", "body", Kind: PostKind.Page));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
            await db.Posts.Where(p => p.Id == page.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.AnnounceMastodon, true).SetProperty(p => p.AnnounceBluesky, true));
        }

        await PublishAsync(admin, page);

        (await RowsAsync(factory, page.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Unpublishing_CancelsPendingRows_AndRepublishingQueuesThemAgain_KeepingTheAttempts()
    {
        await using var factory = Factory();
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreateAsync(admin, "Queue cancel");
        await PublishAsync(admin, post);
        await MutateRowsAsync(factory, post.Id, r => r.Attempts = 2);

        (await admin.PostAsync($"/api/posts/{post.Id}/unpublish", null)).EnsureSuccessStatusCode();

        var cancelled = await RowsAsync(factory, post.Id);
        cancelled.Should().OnlyContain(r => r.State == AnnouncementState.Cancelled && r.NextAttemptAt == null);

        await PublishAsync(admin, post);

        var again = await RowsAsync(factory, post.Id);
        again.Should().HaveCount(2).And.OnlyContain(r => r.State == AnnouncementState.Pending && r.NextAttemptAt != null && r.Attempts == 2,
            "a cancelled row is queued again; its attempts stay, so the next try checks before it sends");
    }

    [Fact]
    public async Task APostedRow_IsNeverQueuedAgain_AndAFailedRowWaitsForRetry()
    {
        await using var factory = Factory();
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreateAsync(admin, "Queue posted");
        await PublishAsync(admin, post);
        await MutateRowsAsync(factory, post.Id, r =>
        {
            if (r.Platform == SocialPlatform.Mastodon) { r.State = AnnouncementState.Posted; r.NextAttemptAt = null; r.RemoteId = "1"; }
            else { r.State = AnnouncementState.Failed; r.NextAttemptAt = null; r.LastError = "refused"; }
        });

        (await admin.PostAsync($"/api/posts/{post.Id}/unpublish", null)).EnsureSuccessStatusCode();
        await PublishAsync(admin, post);

        var rows = await RowsAsync(factory, post.Id);
        rows.Single(r => r.Platform == SocialPlatform.Mastodon).State.Should().Be(AnnouncementState.Posted);
        rows.Single(r => r.Platform == SocialPlatform.Bluesky).State.Should().Be(AnnouncementState.Failed);
    }

    [Fact]
    public async Task AScheduledPost_PublishedByTheScheduler_IsQueuedInTheSameTransaction()
    {
        await using var factory = Factory();
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreateAsync(admin, "Queue scheduled");
        var due = DateTime.UtcNow.AddHours(1);
        (await admin.PostAsJsonAsync($"/api/posts/{post.Id}/publish", new PublishRequest(due))).EnsureSuccessStatusCode();
        (await RowsAsync(factory, post.Id)).Should().BeEmpty("scheduling is not publishing");

        await TickAsync(factory, new Fixtures.FakeTimeProvider(new DateTimeOffset(due.AddMinutes(1))), TestCache.NewNotifier());

        (await RowsAsync(factory, post.Id)).Select(r => r.State).Should().Equal(AnnouncementState.Pending, AnnouncementState.Pending);
    }

    [Fact]
    public async Task IfTheSchedulersTransactionRollsBack_NoRowsRemain()
    {
        await using var factory = Factory();
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreateAsync(admin, "Queue rollback");
        var due = DateTime.UtcNow.AddHours(1);
        (await admin.PostAsJsonAsync($"/api/posts/{post.Id}/publish", new PublishRequest(due))).EnsureSuccessStatusCode();

        var tick = () => TickAsync(factory, new Fixtures.FakeTimeProvider(new DateTimeOffset(due.AddMinutes(1))), new FailingNotifier());
        await tick.Should().ThrowAsync<InvalidOperationException>();

        (await RowsAsync(factory, post.Id)).Should().BeEmpty("the rows and the status change are one transaction");
        await using var scope = factory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<BlogDbContext>().Posts.AsNoTracking().SingleAsync(p => p.Id == post.Id)).Status.Should().Be(PostStatus.Scheduled);
    }

    // --- the endpoints ---

    [Fact]
    public async Task Get_ReturnsTheReadinessTheAddressAndTheRows()
    {
        await using var factory = Factory(bluesky: false);
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreateAsync(admin, "Endpoint get");
        await PublishAsync(admin, post);

        var dto = (await admin.GetFromJsonAsync<PostAnnouncementsDto>($"/api/posts/{post.Id}/announcements"))!;

        dto.MastodonReady.Should().BeTrue();
        dto.BlueskyReady.Should().BeFalse();
        dto.PostUrl.Should().Be($"https://blog.example/post/{post.Slug}");
        dto.Items.Should().ContainSingle().Which.Should().Match<AnnouncementStatusDto>(i => i.Platform == SocialPlatform.Mastodon && i.State == AnnouncementState.Pending && i.Attempts == 0);

        var editor = factory.CreateClient().WithRole("Editor");
        (await editor.GetAsync($"/api/posts/{post.Id}/announcements")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.GetAsync($"/api/posts/{Guid.NewGuid()}/announcements")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Post_QueuesThePublishedPostAgain_Accepted()
    {
        await using var factory = Factory();
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreateAsync(admin, "Endpoint announce", new CreatePostRequest("Endpoint announce", "body", AnnounceMastodon: false, AnnounceBluesky: false));
        await PublishAsync(admin, post);
        (await RowsAsync(factory, post.Id)).Should().BeEmpty("both flags are off");

        var response = await admin.PostAsync($"/api/posts/{post.Id}/announcements/mastodon", null);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var dto = (await response.Content.ReadFromJsonAsync<AnnouncementStatusDto>())!;
        dto.Platform.Should().Be(SocialPlatform.Mastodon);
        dto.State.Should().Be(AnnouncementState.Pending);
        dto.NextAttemptAt.Should().NotBeNull();
        (await RowsAsync(factory, post.Id)).Should().ContainSingle().Which.State.Should().Be(AnnouncementState.Pending);
    }

    [Fact]
    public async Task Post_OnAFailedRow_IsARetry_ItStartsTheAttemptsAgain()
    {
        await using var factory = Factory();
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreateAsync(admin, "Endpoint retry");
        await PublishAsync(admin, post);
        await MutateRowsAsync(factory, post.Id, r => { r.State = AnnouncementState.Failed; r.Attempts = 3; r.NextAttemptAt = null; r.LastError = "refused"; r.QueuedAt = r.QueuedAt.AddDays(-1); });

        var response = await admin.PostAsync($"/api/posts/{post.Id}/announcements/bluesky", null);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var row = (await RowsAsync(factory, post.Id)).Single(r => r.Platform == SocialPlatform.Bluesky);
        row.State.Should().Be(AnnouncementState.Pending);
        row.Attempts.Should().Be(0, "a manual retry starts the attempts again, so a failed try waits the first backoff step");
        row.NextAttemptAt.Should().NotBeNull();
        row.QueuedAt.Should().BeAfter(DateTime.UtcNow.AddMinutes(-5), "the retry window starts again");
    }

    [Fact]
    public async Task MastodonWithoutAnAccount_IsNotReady_NothingIsQueued_AndTheTabSaysNotSetUp()
    {
        await using var factory = SocialApiFactory.Create(fixture, configuration: new(Ready(bluesky: false)) { ["Social:Mastodon:Account"] = "" });
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreateAsync(admin, "No account");

        await PublishAsync(admin, post);

        (await RowsAsync(factory, post.Id)).Should().BeEmpty("without the account the earlier-send check cannot run");
        var dto = (await admin.GetFromJsonAsync<PostAnnouncementsDto>($"/api/posts/{post.Id}/announcements"))!;
        dto.MastodonReady.Should().BeFalse("the Discussion tab then says \"Not set up on this site\"");
        (await admin.PostAsync($"/api/posts/{post.Id}/announcements/mastodon", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Post_Refusals_AreTheDocumentedCodes()
    {
        await using var factory = Factory(bluesky: false);
        var admin = factory.CreateClient().WithRole("Admin");

        var draft = await CreateAsync(admin, "Refuse draft");
        (await admin.PostAsync($"/api/posts/{draft.Id}/announcements/mastodon", null)).StatusCode.Should().Be(HttpStatusCode.Conflict, "the post is not published");

        var page = await CreateAsync(admin, "Refuse page", new CreatePostRequest("Refuse page", "body", Kind: PostKind.Page));
        await PublishAsync(admin, page);
        (await admin.PostAsync($"/api/posts/{page.Id}/announcements/mastodon", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest, "pages are never announced");

        var post = await CreateAsync(admin, "Refuse others");
        await PublishAsync(admin, post);
        (await admin.PostAsync($"/api/posts/{post.Id}/announcements/bluesky", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest, "Bluesky is not set up");

        await MutateRowsAsync(factory, post.Id, r => { r.State = AnnouncementState.Posted; r.NextAttemptAt = null; });
        (await admin.PostAsync($"/api/posts/{post.Id}/announcements/mastodon", null)).StatusCode.Should().Be(HttpStatusCode.Conflict, "a posted row is never sent again");

        var linked = await CreateAsync(admin, "Refuse linked", new CreatePostRequest("Refuse linked", "body", MastodonUrl: GoodMastodon));
        await PublishAsync(admin, linked);
        (await admin.PostAsync($"/api/posts/{linked.Id}/announcements/mastodon", null)).StatusCode.Should().Be(HttpStatusCode.Conflict, "the post already has a link for that network");

        (await admin.PostAsync($"/api/posts/{Guid.NewGuid()}/announcements/mastodon", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.PostAsync($"/api/posts/{post.Id}/announcements/nowhere", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Post_IsForAdminsOnly()
    {
        await using var factory = Factory();
        var post = await CreateAsync(factory.CreateClient().WithRole("Admin"), "Endpoint roles");

        (await factory.CreateClient().WithRole("Editor").PostAsync($"/api/posts/{post.Id}/announcements/mastodon", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static async Task<PostDto> CreateAsync(HttpClient admin, string title, CreatePostRequest? request = null)
    {
        title = $"{title} {Guid.NewGuid():N}";
        var response = await admin.PostAsJsonAsync("/api/posts", (request ?? new CreatePostRequest(title, "body")) with { Title = title });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PostDto>())!;
    }

    private static async Task PublishAsync(HttpClient admin, PostDto post)
    {
        var response = await admin.PostAsJsonAsync($"/api/posts/{post.Id}/publish", new PublishRequest());
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task<List<SocialAnnouncement>> RowsAsync(BlogApiFactory factory, Guid postId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<BlogDbContext>().SocialAnnouncements.AsNoTracking()
            .Where(a => a.PostId == postId).OrderBy(a => a.Platform).ToListAsync();
    }

    private static async Task MutateRowsAsync(BlogApiFactory factory, Guid postId, Action<SocialAnnouncement> change)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        foreach (var row in await db.SocialAnnouncements.Where(a => a.PostId == postId).ToListAsync())
            change(row);
        await db.SaveChangesAsync();
    }

    private static async Task TickAsync(BlogApiFactory factory, TimeProvider time, ICacheNotifier notifier)
    {
        var publisher = new ScheduledPublisherHostedService(
            factory.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ScheduledPublisherHostedService>.Instance, time,
            new ScheduledPublishWakeup(), notifier, factory.Services.GetRequiredService<IOptions<SocialOptions>>());
        await using var scope = factory.Services.CreateAsyncScope();
        await publisher.PublishDuePostsAsync(scope.ServiceProvider.GetRequiredService<BlogDbContext>());
    }

    /// <summary>A notifier whose NOTIFY fails: the scheduler's transaction must then take its rows back.</summary>
    private sealed class FailingNotifier : ICacheNotifier
    {
        public Task SaveAndNotifyAsync(BlogDbContext db, CacheGroup changed, CancellationToken ct = default) => throw new InvalidOperationException("notify failed");

        public Task NotifyAsync(DbContext db, CacheGroup changed, CancellationToken ct = default) => throw new InvalidOperationException("notify failed");

        public Task InvalidateLocalAsync(CacheGroup changed) => Task.CompletedTask;
    }
}
