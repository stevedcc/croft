using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Croft.Api.Models;
using Croft.Api.Services;
using Croft.Api.Social;
using Croft.Api.Tests.Fixtures;
using Croft.Api.Tests.Fixtures.Social;
using Croft.Shared.Contracts.Social;
using Croft.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Croft.Api.Tests.Social;

/// <summary>
/// Brief 12d item 5: the sender. It claims due announcements under the job lock, posts outside it, and records each result in a short transaction:
/// posted (and the post gets its links), retry with backoff, or failed. Stub networks; the clock is fake.
/// </summary>
public class SocialAnnounceTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private static AnnounceOutcome Retry(string reason = "Mastodon answered HTTP 503", DateTime? notBefore = null) => new AnnounceOutcome.Retry(reason, notBefore);

    private static Task<AnnounceOutcome> Answer(AnnounceOutcome outcome) => Task.FromResult(outcome);

    // --- posting ---

    [Fact]
    public async Task APublishedPost_IsPostedOnTheNextTick_ItsLinksAndThreadsAppear()
    {
        await using var h = await AnnounceHarness.CreateAsync(fixture);
        var postId = await h.SeedPostAsync("A post", "Sub", slug: "a-post");
        var replica = h.NewReplica();

        var result = await replica.TickAsync();

        result.Should().Be(new AnnounceTickResult(Skipped: false, Claimed: 2));
        var mastodon = await h.RowAsync(postId, SocialPlatform.Mastodon);
        var bluesky = await h.RowAsync(postId, SocialPlatform.Bluesky);
        mastodon.State.Should().Be(AnnouncementState.Posted);
        mastodon.Attempts.Should().Be(1);
        mastodon.RemoteId.Should().Be("115000000000000001");
        mastodon.RemoteUrl.Should().Be("https://mastodon.example/@owner/115000000000000001");
        mastodon.PostedAt.Should().Be(h.Now);
        mastodon.SentText.Should().Be("A post\n\nSub\n\nhttps://blog.example/post/a-post");
        (mastodon.NextAttemptAt, mastodon.LeaseUntil, mastodon.LastError).Should().Be((null, null, null));
        bluesky.State.Should().Be(AnnouncementState.Posted);
        bluesky.RemoteUrl.Should().Be($"https://bsky.app/profile/{SocialTestSettings.Did}/post/3m5aaaaaaaaa01");

        var post = await h.PostAsync(postId);
        (post.MastodonUrl, post.MastodonStatusId).Should().Be(("https://mastodon.example/@owner/115000000000000001", "115000000000000001"));
        (post.BlueskyUrl, post.BlueskyUri).Should().Be(($"https://bsky.app/profile/{SocialTestSettings.Did}/post/3m5aaaaaaaaa01", $"at://{SocialTestSettings.Did}/app.bsky.feed.post/3m5aaaaaaaaa01"));
        post.DiscussionLinkedAt.Should().Be(h.Now);
        await using var db = h.Database.CreateContext();
        (await db.SocialThreads.AsNoTracking().Where(t => t.PostId == postId).Select(t => t.Platform).ToListAsync()).Should().BeEquivalentTo([SocialPlatform.Mastodon, SocialPlatform.Bluesky]);
        replica.Notifier.Notified.Should().Contain(CacheGroup.All | CacheGroup.Discussion, "a link change invalidates the posts and the discussion");
        replica.Notifier.ClearedLocally.Should().Contain(CacheGroup.All | CacheGroup.Discussion);
    }

    [Fact]
    public async Task TheText_IsComposedAtSendTime_FromThePostsCurrentFields()
    {
        await using var h = await AnnounceHarness.CreateAsync(fixture);
        var postId = await h.SeedPostAsync("Old title", "Old sub", slug: "edited-post");
        await h.ChangePostAsync(postId, p => { p.Title = "New title"; p.Subtitle = null; });
        var replica = h.NewReplica();

        await replica.TickAsync();

        h.Mastodon.Sends.Should().ContainSingle().Which.Text.Should().Be("New title\n\nhttps://blog.example/post/edited-post");
        h.Bluesky.Sends.Single().Text.Should().Be(h.Mastodon.Sends.Single().Text);
        h.Mastodon.Sends.Single().Should().Match<AnnouncementMessage>(m => m.PostUrl == "https://blog.example/post/edited-post" && m.Title == "New title" && m.Subtitle == null);
    }

    [Fact]
    public async Task ACustomText_IsSentAsItIs_WithTheAddressAtTheEnd_AndTheRowIdIsTheMessageId()
    {
        await using var h = await AnnounceHarness.CreateAsync(fixture);
        var postId = await h.SeedPostAsync(slug: "custom", announcementText: "My words #homelab");
        var replica = h.NewReplica();

        await replica.TickAsync();

        var row = await h.RowAsync(postId, SocialPlatform.Mastodon);
        var sent = h.Mastodon.Sends.Single();
        sent.Text.Should().Be("My words #homelab\n\nhttps://blog.example/post/custom");
        sent.RowId.Should().Be(row.Id, "the row id is Mastodon's Idempotency-Key");
    }

    [Fact]
    public async Task ALongDefault_IsShortenedToFit_NeverFails()
    {
        await using var h = await AnnounceHarness.CreateAsync(fixture);
        var postId = await h.SeedPostAsync(subtitle: new string('s', 290), slug: "long");
        var replica = h.NewReplica();

        await replica.TickAsync();

        (await h.RowAsync(postId, SocialPlatform.Bluesky)).State.Should().Be(AnnouncementState.Posted);
        Croft.Shared.Social.AnnouncementText.BlueskyLength(h.Bluesky.Sends.Single().Text).Should().Be(300);
    }

    [Fact]
    public async Task ACustomTextOverTheLimit_FailsAtOnce_WithoutSending()
    {
        await using var h = await AnnounceHarness.CreateAsync(fixture);
        var postId = await h.SeedPostAsync(slug: "toolong", announcementText: new string('x', 400));
        var replica = h.NewReplica();

        await replica.TickAsync();

        var bluesky = await h.RowAsync(postId, SocialPlatform.Bluesky);
        bluesky.State.Should().Be(AnnouncementState.Failed);
        bluesky.LastError.Should().StartWith("AnnouncementText is too long for Bluesky (");
        bluesky.NextAttemptAt.Should().BeNull();
        h.Bluesky.Sends.Should().BeEmpty();
        (await h.RowAsync(postId, SocialPlatform.Mastodon)).State.Should().Be(AnnouncementState.Posted, "400 characters fit Mastodon's 500");
    }

    [Fact]
    public async Task TheBanner_IsHandedOverToBeReadOnDemand_FromFileStorage()
    {
        await using var h = await AnnounceHarness.CreateAsync(fixture);
        var bytes = TestImages.Gradient(SKEncodedImageFormat.Png, 50, 50);
        var banner = await h.SeedBannerAsync("global/banner.png", bytes);
        await h.SeedPostAsync(bannerId: banner);
        var replica = h.NewReplica();

        await replica.TickAsync();

        var loader = h.Bluesky.Sends.Single().LoadBanner;
        loader.Should().NotBeNull();
        (await loader!(CancellationToken.None)).Should().Equal(bytes);
    }

    [Fact]
    public async Task APostWithoutABanner_HasNoBannerLoader()
    {
        await using var h = await AnnounceHarness.CreateAsync(fixture);
        await h.SeedPostAsync();

        await h.NewReplica().TickAsync();

        h.Bluesky.Sends.Single().LoadBanner.Should().BeNull();
    }

    [Fact]
    public async Task ABannerFileThatIsGone_IsNullNotAnError()
    {
        await using var h = await AnnounceHarness.CreateAsync(fixture);
        var banner = await h.SeedBannerAsync("global/gone.png", [1, 2, 3]);
        h.Storage.Files.Clear();
        await h.SeedPostAsync(bannerId: banner);

        await h.NewReplica().TickAsync();

        (await h.Bluesky.Sends.Single().LoadBanner!(CancellationToken.None)).Should().BeNull();
    }

    // --- who is claimed ---

    [Fact]
    public async Task OnlyDueUnleasedPendingRowsOfPublishedPosts_AreClaimed()
    {
        await using var h = await AnnounceHarness.CreateAsync(fixture);
        var later = await h.SeedPostAsync(slug: "later");
        await h.ChangeRowAsync(later, SocialPlatform.Mastodon, r => r.NextAttemptAt = h.Now.AddMinutes(5));
        var leased = await h.SeedPostAsync(slug: "leased");
        await h.ChangeRowAsync(leased, SocialPlatform.Mastodon, r => r.LeaseUntil = h.Now.AddMinutes(5));
        var done = await h.SeedPostAsync(slug: "done");
        await h.ChangeRowAsync(done, SocialPlatform.Mastodon, r => { r.State = AnnouncementState.Posted; r.NextAttemptAt = null; });
        var failed = await h.SeedPostAsync(slug: "failed");
        await h.ChangeRowAsync(failed, SocialPlatform.Mastodon, r => { r.State = AnnouncementState.Failed; r.NextAttemptAt = null; });
        var cancelled = await h.SeedPostAsync(slug: "cancelled");
        await h.ChangeRowAsync(cancelled, SocialPlatform.Mastodon, r => { r.State = AnnouncementState.Cancelled; r.NextAttemptAt = null; });
        var draft = await h.SeedPostAsync(slug: "draft", status: PostStatus.Draft);

        await h.NewReplica().TickAsync();

        // Only the Bluesky rows (and none of the Mastodon rows above) were claimed; the draft's rows are not claimed at all.
        h.Mastodon.Sends.Should().BeEmpty();
        (await h.RowAsync(draft, SocialPlatform.Bluesky)).Attempts.Should().Be(0);
        (await h.RowAsync(later, SocialPlatform.Mastodon)).Attempts.Should().Be(0);
    }

    [Fact]
    public async Task AtMostFiveRowsAreClaimedPerTick_TheOldestFirst()
    {
        await using var h = await AnnounceHarness.CreateAsync(fixture);
        List<Guid> ids = [];
        for (var i = 0; i < 4; i++)
        {
            ids.Add(await h.SeedPostAsync(slug: $"many-{i}"));
            h.Time.Advance(TimeSpan.FromSeconds(1));
        }

        var first = await h.NewReplica().TickAsync();
        var second = await h.NewReplica().TickAsync();

        first.Claimed.Should().Be(5);
        second.Claimed.Should().Be(3, "8 rows in all");
        h.Mastodon.Sends.Select(s => s.PostUrl).Should().Equal(Enumerable.Range(0, 4).Select(i => $"https://blog.example/post/many-{i}"), "the oldest rows go first");
        foreach (var id in ids)
            (await h.RowAsync(id, SocialPlatform.Mastodon)).State.Should().Be(AnnouncementState.Posted);
    }

    [Fact]
    public async Task ANetworkThatIsNotReady_LeavesItsRowsPending_AndUntouched()
    {
        await using var h = await AnnounceHarness.CreateAsync(fixture);
        h.Settings.Bluesky.AppPassword = "";
        var postId = await h.SeedPostAsync();

        await h.NewReplica().TickAsync();

        var bluesky = await h.RowAsync(postId, SocialPlatform.Bluesky);
        (bluesky.State, bluesky.Attempts, bluesky.LeaseUntil, bluesky.LastError).Should().Be((AnnouncementState.Pending, 0, null, null));
        h.Bluesky.Sends.Should().BeEmpty();
        (await h.RowAsync(postId, SocialPlatform.Mastodon)).State.Should().Be(AnnouncementState.Posted);
    }

    [Fact]
    public async Task WithSocialOff_ATickDoesNothing()
    {
        await using var h = await AnnounceHarness.CreateAsync(fixture);
        h.Settings.Enabled = false;
        var postId = await h.SeedPostAsync();

        var result = await h.NewReplica().TickAsync();

        result.Should().Be(new AnnounceTickResult(Skipped: true, Claimed: 0));
        (await h.RowAsync(postId, SocialPlatform.Mastodon)).Attempts.Should().Be(0);
    }

    // --- exactly once ---

    [Fact]
    public async Task TwoReplicasTickingAtOnce_SendOnce()
    {
        await using var h = await AnnounceHarness.CreateAsync(fixture);
        var postId = await h.SeedPostAsync(bluesky: false);
        var gate = new TaskCompletionSource();
        var arrived = new TaskCompletionSource();
        h.Mastodon.OnSend = async (_, _) =>
        {
            arrived.TrySetResult();
            await gate.Task;
            return h.Mastodon.NewPost();
        };
        var first = h.NewReplica();
        var second = h.NewReplica();

        var firstTick = first.TickAsync();
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var secondResult = await second.TickAsync();
        gate.SetResult();
        await firstTick;

        secondResult.Claimed.Should().Be(0, "the first replica's lease holds the row");
        h.Mastodon.Sends.Should().ContainSingle();
        (await h.RowAsync(postId, SocialPlatform.Mastodon)).State.Should().Be(AnnouncementState.Posted);
    }

    [Fact]
    public async Task AfterACrash_TheLeaseRunsOut_AndTheNextAttemptChecks_FindsThePost_AndDoesNotSendAgain()
    {
        await using var h = await AnnounceHarness.CreateAsync(fixture);
        var postId = await h.SeedPostAsync(slug: "crash", bluesky: false);
        using var cts = new CancellationTokenSource();
        h.Mastodon.OnSend = (_, ct) =>
        {
            // The post reaches the network, then the process dies before the result is written (shutdown stands in for the crash).
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Answer(h.Mastodon.NewPost());
        };
        var replica = h.NewReplica();

        var crashed = () => replica.TickAsync(cts.Token);
        await crashed.Should().ThrowAsync<OperationCanceledException>();

        var claimed = await h.RowAsync(postId, SocialPlatform.Mastodon);
        (claimed.State, claimed.Attempts).Should().Be((AnnouncementState.Pending, 1));
        claimed.LeaseUntil.Should().Be(h.Now.AddMinutes(5));
        h.Time.Advance(TimeSpan.FromMinutes(6));
        var onTheNetwork = new AnnounceOutcome.Posted("115000000000000777", "https://mastodon.example/@owner/115000000000000777");
        h.Mastodon.OnCheck = (_, _) => Answer(onTheNetwork);
        h.Mastodon.OnSend = null;

        await h.NewReplica().TickAsync();

        h.Mastodon.Checks.Should().ContainSingle().Which.PostUrl.Should().Be("https://blog.example/post/crash");
        h.Mastodon.Sends.Should().ContainSingle("the second attempt found the post and did not send");
        var row = await h.RowAsync(postId, SocialPlatform.Mastodon);
        (row.State, row.Attempts, row.RemoteId, row.RemoteUrl).Should().Be((AnnouncementState.Posted, 2, "115000000000000777", "https://mastodon.example/@owner/115000000000000777"));
        (await h.PostAsync(postId)).MastodonStatusId.Should().Be("115000000000000777", "the post gets the link of the found status");
    }

    [Fact]
    public async Task AfterACrash_WhenTheCheckFindsNothing_TheAttemptSends()
    {
        await using var h = await AnnounceHarness.CreateAsync(fixture);
        var postId = await h.SeedPostAsync(bluesky: false);
        await h.ChangeRowAsync(postId, SocialPlatform.Mastodon, r => { r.Attempts = 1; r.LeaseUntil = h.Now.AddMinutes(-1); });

        await h.NewReplica().TickAsync();

        h.Mastodon.Checks.Should().ContainSingle();
        h.Mastodon.Sends.Should().ContainSingle();
        (await h.RowAsync(postId, SocialPlatform.Mastodon)).State.Should().Be(AnnouncementState.Posted);
    }

    [Fact]
    public async Task TheFirstAttempt_DoesNotCheck()
    {
        await using var h = await AnnounceHarness.CreateAsync(fixture);
        await h.SeedPostAsync();

        await h.NewReplica().TickAsync();

        h.Mastodon.Checks.Should().BeEmpty();
        h.Bluesky.Checks.Should().BeEmpty();
    }

    [Fact]
    public async Task ATroubledCheck_BacksOffInsteadOfSendingBlind()
    {
        await using var h = await AnnounceHarness.CreateAsync(fixture);
        var postId = await h.SeedPostAsync(bluesky: false);
        await h.ChangeRowAsync(postId, SocialPlatform.Mastodon, r => r.Attempts = 1);
        h.Mastodon.OnCheck = (_, _) => Answer(Retry("Mastodon answered HTTP 503"));

        await h.NewReplica().TickAsync();

        h.Mastodon.Sends.Should().BeEmpty();
        var row = await h.RowAsync(postId, SocialPlatform.Mastodon);
        (row.State, row.Attempts, row.LastError, row.NextAttemptAt).Should().Be((AnnouncementState.Pending, 2, "Mastodon answered HTTP 503", h.Now.AddMinutes(2)));
    }

    // --- retries ---

    [Fact]
    public async Task TheBackoff_IsOneTwoFourEightSixteenThirtyTwoThenSixtyMinutes()
    {
        await using var h = await AnnounceHarness.CreateAsync(fixture);
        var postId = await h.SeedPostAsync(bluesky: false);
        h.Mastodon.OnSend = (_, _) => Answer(Retry());
        var replica = h.NewReplica();
        List<int> waits = [];

        for (var attempt = 1; attempt <= 8; attempt++)
        {
            await replica.TickAsync();
            var row = await h.RowAsync(postId, SocialPlatform.Mastodon);
            row.State.Should().Be(AnnouncementState.Pending);
            row.NextAttemptAt.Should().NotBeNull();
            row.Attempts.Should().Be(attempt);
            row.LeaseUntil.Should().BeNull();
            row.LastError.Should().Be("Mastodon answered HTTP 503");
            row.LastAttemptAt.Should().Be(h.Now);
            waits.Add((int)(row.NextAttemptAt!.Value - h.Now).TotalMinutes);
            if (attempt == 1)
            {
                (await replica.TickAsync()).Claimed.Should().Be(0, "it is not due before its time");
            }

            h.Time.Advance(row.NextAttemptAt!.Value - h.Now);
        }

        waits.Should().Equal(1, 2, 4, 8, 16, 32, 60, 60);
    }

    [Fact]
    public async Task ARateLimitReset_WinsWhenItIsLater_AndTheBackoffWhenItIsNot()
    {
        await using var h = await AnnounceHarness.CreateAsync(fixture);
        var late = await h.SeedPostAsync(slug: "late", bluesky: false);
        h.Mastodon.OnSend = (_, _) => Answer(Retry("Mastodon is rate limiting (HTTP 429)", h.Now.AddMinutes(20)));
        await h.NewReplica().TickAsync();
        (await h.RowAsync(late, SocialPlatform.Mastodon)).NextAttemptAt.Should().Be(h.Now.AddMinutes(20));

        var early = await h.SeedPostAsync(slug: "early", bluesky: false);
        await h.ChangeRowAsync(late, SocialPlatform.Mastodon, r => r.NextAttemptAt = h.Now.AddDays(1));
        h.Mastodon.OnSend = (_, _) => Answer(Retry("Mastodon is rate limiting (HTTP 429)", h.Now.AddSeconds(10)));
        await h.NewReplica().TickAsync();
        (await h.RowAsync(early, SocialPlatform.Mastodon)).NextAttemptAt.Should().Be(h.Now.AddMinutes(1));
    }

    [Fact]
    public async Task WhenTheNextAttemptWouldFallAfterSixHours_TheRowFails_WithTheLastError()
    {
        await using var h = await AnnounceHarness.CreateAsync(fixture);
        var postId = await h.SeedPostAsync(bluesky: false);
        var queuedAt = h.Now;
        h.Mastodon.OnSend = (_, _) => Answer(Retry("Mastodon timed out"));
        var replica = h.NewReplica();

        SocialAnnouncement row;
        var ticks = 0;
        do
        {
            await replica.TickAsync();
            row = await h.RowAsync(postId, SocialPlatform.Mastodon);
            if (row.State == AnnouncementState.Pending)
                h.Time.Advance(row.NextAttemptAt!.Value - h.Now);
        }
        while (row.State == AnnouncementState.Pending && ++ticks < 30);

        row.State.Should().Be(AnnouncementState.Failed);
        row.LastError.Should().Be("Gave up after 6 hours: Mastodon timed out");
        row.NextAttemptAt.Should().BeNull();
        row.Attempts.Should().Be(11, "attempts at 0, 1, 3, 7, 15, 31, 63, 123, 183, 243 and 303 minutes; the next would be at 363");
        (h.Now - queuedAt).Should().Be(TimeSpan.FromMinutes(303));
    }

    [Fact]
    public async Task AnErrorThatCannotHeal_FailsAtOnce()
    {
        await using var h = await AnnounceHarness.CreateAsync(fixture);
        var postId = await h.SeedPostAsync(bluesky: false);
        h.Mastodon.OnSend = (_, _) => Answer(new AnnounceOutcome.Failed("Mastodon refused the post (HTTP 401): The access token is invalid"));

        await h.NewReplica().TickAsync();

        var row = await h.RowAsync(postId, SocialPlatform.Mastodon);
        (row.State, row.Attempts, row.LastError, row.NextAttemptAt, row.LeaseUntil).Should().Be(
            (AnnouncementState.Failed, 1, "Mastodon refused the post (HTTP 401): The access token is invalid", null, null));
        (await h.PostAsync(postId)).MastodonUrl.Should().BeNull();
    }

    [Fact]
    public async Task AFailedRowWaitsForRetry_AndRetryPostsIt()
    {
        await using var h = await AnnounceHarness.CreateAsync(fixture);
        var postId = await h.SeedPostAsync(bluesky: false);
        h.Mastodon.OnSend = (_, _) => Answer(new AnnounceOutcome.Failed("refused"));
        var replica = h.NewReplica();
        await replica.TickAsync();
        h.Time.Advance(TimeSpan.FromHours(1));
        (await replica.TickAsync()).Claimed.Should().Be(0);

        // What the Retry endpoint does:
        await h.ChangeRowAsync(postId, SocialPlatform.Mastodon, r => { r.State = AnnouncementState.Pending; r.QueuedAt = h.Now; r.NextAttemptAt = h.Now; });
        h.Mastodon.OnSend = null;
        await replica.TickAsync();

        h.Mastodon.Checks.Should().ContainSingle("a retry checks before it sends: attempts is 2");
        (await h.RowAsync(postId, SocialPlatform.Mastodon)).State.Should().Be(AnnouncementState.Posted);
    }

    [Fact]
    public async Task AnUnexpectedException_IsARetry_RecordedByItsTypeOnly()
    {
        await using var h = await AnnounceHarness.CreateAsync(fixture);
        var postId = await h.SeedPostAsync(bluesky: false);
        h.Mastodon.OnSend = (_, _) => throw new InvalidOperationException("secret detail test-token-0f3a");

        await h.NewReplica().TickAsync();

        var row = await h.RowAsync(postId, SocialPlatform.Mastodon);
        (row.State, row.LastError).Should().Be((AnnouncementState.Pending, "Unexpected error (InvalidOperationException)"));
        h.Logs.Entries.Should().NotContain(e => e.Message.Contains("secret detail", StringComparison.Ordinal));
        h.Logs.Entries.Should().NotContain(e => e.Exception != null, "an exception object would carry its message and stack");
    }

    [Fact]
    public async Task OneRowFailing_DoesNotStopTheOthers()
    {
        await using var h = await AnnounceHarness.CreateAsync(fixture);
        var postId = await h.SeedPostAsync();
        h.Mastodon.OnSend = (_, _) => throw new InvalidOperationException("boom");

        await h.NewReplica().TickAsync();

        (await h.RowAsync(postId, SocialPlatform.Bluesky)).State.Should().Be(AnnouncementState.Posted);
    }

    // --- changes while a send is in flight ---

    [Fact]
    public async Task UnpublishedWhileInFlight_ASuccessIsStillRecordedAsPosted()
    {
        await using var h = await AnnounceHarness.CreateAsync(fixture);
        var postId = await h.SeedPostAsync(bluesky: false);
        h.Mastodon.OnSend = async (_, _) =>
        {
            // The unpublish happens in the middle of the send: it cancels the pending row.
            await h.ChangeRowAsync(postId, SocialPlatform.Mastodon, r => { r.State = AnnouncementState.Cancelled; r.NextAttemptAt = null; });
            return h.Mastodon.NewPost();
        };

        await h.NewReplica().TickAsync();

        var row = await h.RowAsync(postId, SocialPlatform.Mastodon);
        row.State.Should().Be(AnnouncementState.Posted, "it happened");
        row.RemoteId.Should().NotBeNull();
        (await h.PostAsync(postId)).MastodonStatusId.Should().Be(row.RemoteId);
    }

    [Fact]
    public async Task UnpublishedWhileInFlight_AFailureDoesNotBringTheRowBack()
    {
        await using var h = await AnnounceHarness.CreateAsync(fixture);
        var postId = await h.SeedPostAsync(bluesky: false);
        h.Mastodon.OnSend = async (_, _) =>
        {
            await h.ChangeRowAsync(postId, SocialPlatform.Mastodon, r => { r.State = AnnouncementState.Cancelled; r.NextAttemptAt = null; });
            return Retry();
        };

        await h.NewReplica().TickAsync();

        var row = await h.RowAsync(postId, SocialPlatform.Mastodon);
        (row.State, row.NextAttemptAt, row.LeaseUntil).Should().Be((AnnouncementState.Cancelled, null, null));
    }

    [Fact]
    public async Task AHandLinkAddedMeanwhile_IsKept_AndTheRowOnlyRecordsItsRemoteUrl()
    {
        await using var h = await AnnounceHarness.CreateAsync(fixture);
        var postId = await h.SeedPostAsync(bluesky: false);
        const string hand = "https://mastodon.example/@owner/110054900866478823";
        h.Mastodon.OnSend = async (_, _) =>
        {
            await h.ChangePostAsync(postId, p => { p.MastodonUrl = hand; p.MastodonStatusId = "110054900866478823"; });
            return h.Mastodon.NewPost();
        };

        await h.NewReplica().TickAsync();

        var post = await h.PostAsync(postId);
        (post.MastodonUrl, post.MastodonStatusId).Should().Be((hand, "110054900866478823"));
        var row = await h.RowAsync(postId, SocialPlatform.Mastodon);
        (row.State, row.RemoteUrl).Should().Be((AnnouncementState.Posted, "https://mastodon.example/@owner/115000000000000001"));
    }

    [Fact]
    public async Task LinkingOneNetwork_LeavesTheOtherNetworksLinkAlone()
    {
        await using var h = await AnnounceHarness.CreateAsync(fixture);
        var postId = await h.SeedPostAsync(bluesky: false);
        const string blueskyHand = "https://bsky.app/profile/owner.example/post/3lhand";
        await h.ChangePostAsync(postId, p => { p.BlueskyUrl = blueskyHand; p.BlueskyUri = "at://x/app.bsky.feed.post/3lhand"; });

        await h.NewReplica().TickAsync();

        var post = await h.PostAsync(postId);
        post.MastodonStatusId.Should().NotBeNull();
        (post.BlueskyUrl, post.BlueskyUri).Should().Be((blueskyHand, "at://x/app.bsky.feed.post/3lhand"));
    }

    // --- secrets ---

    [Fact]
    public async Task NoSecretReachesALogLine_ARowsError_OrTheDto()
    {
        const string token = "test-token-0f3a";
        const string appPassword = "test-pass-77c1";
        const string jwt = "test-jwt-9d2e.eyJ0ZXN0IjoxfQ.sig";
        await using var h = await AnnounceHarness.CreateAsync(fixture);
        var postId = await h.SeedPostAsync(slug: "secrets");
        var mastodon = new MastodonAnnouncer(new StubClientFactory(StubHandler.Json($$"""{"error":"bad {{token}} here"}""", HttpStatusCode.UnprocessableEntity)), Options.Create(h.Settings), h.Time);
        var pds = new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("createSession", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""{"accessJwt":"{{jwt}}","did":"{{SocialTestSettings.Did}}"}""", Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent($$"""{"error":"InvalidRequest {{jwt}} {{appPassword}}"}""", Encoding.UTF8, "application/json") });
        var bluesky = new BlueskyAnnouncer(new StubClientFactory(pds), Options.Create(h.Settings), h.Time);

        await h.NewReplica(null, mastodon, bluesky).TickAsync();

        var rows = new[] { await h.RowAsync(postId, SocialPlatform.Mastodon), await h.RowAsync(postId, SocialPlatform.Bluesky) };
        rows.Should().OnlyContain(r => r.State == AnnouncementState.Failed);
        var everything = string.Join("\n",
            h.Logs.Entries.Select(e => e.Message + " " + e.Exception + " " + string.Join(",", (e.Values ?? []).Select(v => v.Value))))
            + JsonSerializer.Serialize(rows.Select(r => r.LastError))
            + JsonSerializer.Serialize(rows.Select(r => AnnouncementService.ToDto(r)));
        foreach (var secret in new[] { token, appPassword, jwt })
            everything.Should().NotContain(secret);
        rows.Select(r => r.LastError).Should().OnlyContain(e => e!.StartsWith("Mastodon refused") || e.StartsWith("Bluesky refused"));
    }

    [Fact]
    public async Task TheLogs_NameThePlatformThePostTheAttemptAndTheOutcome_AndNothingMore()
    {
        await using var h = await AnnounceHarness.CreateAsync(fixture);
        var postId = await h.SeedPostAsync(slug: "logs", bluesky: false);
        h.Mastodon.OnSend = (_, _) => Answer(Retry("Mastodon answered HTTP 503 with-reason-text"));

        await h.NewReplica().TickAsync();

        var line = h.Logs.Entries.Single(e => e.Message.Contains(postId.ToString(), StringComparison.Ordinal));
        line.Message.Should().Contain("Mastodon").And.Contain("attempt 1").And.Contain("Retry");
        h.Logs.Entries.Should().NotContain(e => e.Message.Contains("with-reason-text", StringComparison.Ordinal) || e.Message.Contains("blog.example", StringComparison.Ordinal) || e.Message.Contains("Seeded", StringComparison.Ordinal));
    }
}
