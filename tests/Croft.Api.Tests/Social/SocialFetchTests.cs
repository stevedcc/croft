using System.Net;
using AwesomeAssertions;
using Croft.Api.Data;
using Croft.Api.Models;
using Croft.Api.Services;
using Croft.Api.Social;
using Croft.Api.Tests.Fixtures;
using Croft.Api.Tests.Fixtures.Social;
using Croft.Shared.Contracts.Social;
using Croft.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Croft.Api.Tests.Social;

/// <summary>
/// Brief 12a item 3: the fetcher claims the threads that are due under the job lock, fetches them outside it, and stores each result
/// in a short transaction of its own. Stub platforms answer from the recorded fixtures; the clock is a fake; a tick is driven directly.
/// </summary>
public class SocialFetchTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private static readonly DateTimeOffset Start = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static DateTime HoursAgo(double hours) => Start.UtcDateTime.AddHours(-hours);

    private Task<SocialFetchHarness> HarnessAsync(bool enabled = true) => SocialFetchHarness.CreateAsync(fixture, Start, enabled);

    [Fact]
    public async Task ADueThread_IsFetched_AndItsRepliesAreStoredWithTheirParents()
    {
        await using var h = await HarnessAsync();
        var post = await h.SeedPostAsync(HoursAgo(1));
        var replica = h.NewReplica();

        var tick = await replica.TickAsync();

        tick.Should().Be(new SocialTickResult(Skipped: false, Claimed: 1));
        h.Mastodon.ContextRequests.Should().ContainSingle();
        var replies = await h.RepliesAsync(post, SocialPlatform.Mastodon);
        replies.Select(r => (r.ExternalId, r.ParentExternalId)).Should().BeEquivalentTo(
        [
            ("110054913016854354", (string?)null), ("110054956705310518", null),
            ("110054985368879806", "110054956705310518"), ("110055006448893422", "110054985368879806"),
        ]);
        replies.Should().OnlyContain(r => r.FetchedAt == Start.UtcDateTime && r.PostId == post && r.Platform == SocialPlatform.Mastodon);
        var thread = await h.ThreadAsync(post, SocialPlatform.Mastodon);
        thread.LastSuccessAt.Should().Be(Start.UtcDateTime);
        thread.LastAttemptAt.Should().Be(Start.UtcDateTime);
        thread.LastError.Should().BeNull();
        thread.SnapshotHash.Should().MatchRegex("^[0-9a-f]{64}$");
        thread.LeaseUntil.Should().BeNull("the lease is released with the result");
        thread.NextDueAt.Should().Be(Start.UtcDateTime.AddMinutes(10));
        replica.Notifier.Notified.Should().Equal(CacheGroup.Discussion);
        replica.Notifier.ClearedLocally.Should().Equal(CacheGroup.Discussion);
    }

    [Fact]
    public async Task BothPlatforms_AreFetchedInOneTick()
    {
        await using var h = await HarnessAsync();
        var post = await h.SeedPostAsync(HoursAgo(1), blueskyUri: BlueskyThreadBuilder.RootUri);

        var tick = await h.NewReplica().TickAsync();

        tick.Claimed.Should().Be(2);
        (await h.RepliesAsync(post, SocialPlatform.Mastodon)).Should().HaveCount(4);
        var bluesky = await h.RepliesAsync(post, SocialPlatform.Bluesky);
        bluesky.Select(r => (r.ExternalId, r.ParentExternalId)).Should().BeEquivalentTo(
        [
            ("at://did:plc:alpha/app.bsky.feed.post/alpha", (string?)null),
            ("at://did:plc:alpha1/app.bsky.feed.post/alpha1", "at://did:plc:alpha/app.bsky.feed.post/alpha"),
            ("at://did:plc:beta/app.bsky.feed.post/beta", null),
        ]);
    }

    [Theory]
    [InlineData(1.0, 10)]
    [InlineData(72.0, 60)]
    [InlineData(40 * 24.0, 24 * 60)]
    public async Task TheNextFetch_IsDueAfterTheIntervalOfTheDiscussionsAge(double ageHours, int expectedMinutes)
    {
        await using var h = await HarnessAsync();
        var post = await h.SeedPostAsync(HoursAgo(ageHours));

        await h.NewReplica().TickAsync();

        (await h.ThreadAsync(post, SocialPlatform.Mastodon)).NextDueAt.Should().Be(Start.UtcDateTime.AddMinutes(expectedMinutes));
    }

    [Fact]
    public async Task APostPublishedLongAgo_ButLinkedAnHourAgo_IsFetchedEveryTenMinutes()
    {
        await using var h = await HarnessAsync();
        var post = await h.SeedPostAsync(HoursAgo(100 * 24), linkedAt: HoursAgo(1));

        await h.NewReplica().TickAsync();

        (await h.ThreadAsync(post, SocialPlatform.Mastodon)).NextDueAt.Should().Be(Start.UtcDateTime.AddMinutes(10),
            "the schedule's clock is the later of the publication and the link");
    }

    [Fact]
    public async Task AThreadThatIsNotYetDue_IsLeftAlone()
    {
        await using var h = await HarnessAsync();
        await h.SeedPostAsync(HoursAgo(1), nextDueAt: Start.UtcDateTime.AddMinutes(1));

        var tick = await h.NewReplica().TickAsync();

        tick.Claimed.Should().Be(0);
        h.Mastodon.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task AThreadWithALiveLease_IsLeftAlone_AndOneWithAnExpiredLeaseIsClaimedAgain()
    {
        await using var h = await HarnessAsync();
        var leased = await h.SeedPostAsync(HoursAgo(1));
        var abandoned = await h.SeedPostAsync(HoursAgo(1));
        await h.UpdateThreadAsync(leased, SocialPlatform.Mastodon, t => t.LeaseUntil = Start.UtcDateTime.AddMinutes(3));
        await h.UpdateThreadAsync(abandoned, SocialPlatform.Mastodon, t => t.LeaseUntil = Start.UtcDateTime.AddMinutes(-1));

        var tick = await h.NewReplica().TickAsync();

        tick.Claimed.Should().Be(1, "only the thread whose replica died (its lease ran out) is taken over");
        (await h.RepliesAsync(abandoned, SocialPlatform.Mastodon)).Should().HaveCount(4);
        (await h.RepliesAsync(leased, SocialPlatform.Mastodon)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(PostStatus.Draft)]
    [InlineData(PostStatus.Scheduled)]
    public async Task AnUnpublishedPost_IsNeverFetched(PostStatus status)
    {
        await using var h = await HarnessAsync();
        await h.SeedPostAsync(HoursAgo(1), blueskyUri: BlueskyThreadBuilder.RootUri, status: status);

        var tick = await h.NewReplica().TickAsync();

        tick.Claimed.Should().Be(0);
        h.Mastodon.Requests.Should().BeEmpty();
        h.Bluesky.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task WithSocialDisabled_NothingIsFetched()
    {
        await using var h = await HarnessAsync(enabled: false);
        await h.SeedPostAsync(HoursAgo(1));

        var tick = await h.NewReplica().TickAsync();

        tick.Should().Be(new SocialTickResult(Skipped: true, Claimed: 0));
        h.Mastodon.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task WithSocialDisabled_TheTickRunsNoSql_ItIsOneCheapCallEveryMinute()
    {
        await using var h = await HarnessAsync(enabled: false);
        var counter = new SqlCommandCounter();
        await using var db = new BlogDbContext(new DbContextOptionsBuilder<BlogDbContext>()
            .UseNpgsql(h.Database.ConnectionString).AddInterceptors(counter).Options);

        await h.NewReplica().TickAsync(db);

        counter.Commands.Should().Be(0);
    }

    [Theory]
    [InlineData(true, "", "did:plc:x", new[] { "Mastodon is disabled because Social:Mastodon:Server is empty" })]
    [InlineData(true, "mastodon.example", "", new[] { "Bluesky is disabled because Social:Bluesky:Did is empty" })]
    [InlineData(true, "", "", new[] { "Mastodon is disabled because Social:Mastodon:Server is empty", "Bluesky is disabled because Social:Bluesky:Did is empty" })]
    [InlineData(true, "mastodon.example", "did:plc:x", new string[0])]
    [InlineData(false, "", "", new string[0])]
    public async Task AtStart_APlatformDisabledByAnEmptySetting_IsLoggedOnce_AtInformation(bool enabled, string server, string did, string[] expected)
    {
        await using var h = await HarnessAsync(enabled);
        h.Settings.Mastodon.Server = server;
        h.Settings.Bluesky.Did = did;
        var logs = new LogCollector();
        using var loggers = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var replica = h.NewReplica(logger: loggers.CreateLogger<SocialFetchHostedService>());

        await replica.Service.StartAsync(CancellationToken.None);
        await replica.Service.StopAsync(CancellationToken.None);

        var messages = logs.Entries.Where(e => e.Message.Contains("disabled", StringComparison.Ordinal)).ToList();
        messages.Should().OnlyContain(e => e.Level == LogLevel.Information);
        messages.Select(e => e.Message).Should().HaveCount(expected.Length);
        foreach (var text in expected)
            messages.Should().ContainSingle(e => e.Message.Contains(text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task APlatformWithNoServerOrDid_IsNotFetched_TheOtherIs()
    {
        await using var h = await HarnessAsync();
        h.Settings.Mastodon.Server = "";
        var post = await h.SeedPostAsync(HoursAgo(1), blueskyUri: BlueskyThreadBuilder.RootUri);

        var tick = await h.NewReplica().TickAsync();

        tick.Claimed.Should().Be(1);
        h.Mastodon.Requests.Should().BeEmpty();
        (await h.RepliesAsync(post, SocialPlatform.Bluesky)).Should().NotBeEmpty();
    }

    [Fact]
    public async Task EnabledWithNeitherServerNorDid_NothingIsFetched_NoRequestGoesOut()
    {
        // The E2E instances run like this (brief 12b item 5): Social:Enabled on, no Mastodon server, no Bluesky DID,
        // so the API shows what the tests stored and never calls a platform, however due a linked thread is.
        await using var h = await HarnessAsync();
        h.Settings.Mastodon.Server = "";
        h.Settings.Bluesky.Did = "";
        await h.SeedPostAsync(HoursAgo(1), blueskyUri: BlueskyThreadBuilder.RootUri);

        var tick = await h.NewReplica().TickAsync();

        tick.Should().Be(new SocialTickResult(Skipped: true, Claimed: 0));
        h.Mastodon.Requests.Should().BeEmpty();
        h.Bluesky.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task WhileAnotherReplicaHoldsTheJobLock_TheTickIsSkipped()
    {
        await using var h = await HarnessAsync();
        await h.SeedPostAsync(HoursAgo(1));
        await using var holder = new NpgsqlConnection(h.Database.ConnectionString);
        await holder.OpenAsync();
        await using (var lockCommand = holder.CreateCommand())
        {
            lockCommand.CommandText = $"SELECT pg_advisory_lock({BackgroundJobIds.SocialFetch})";
            await lockCommand.ExecuteNonQueryAsync();
        }

        var tick = await h.NewReplica().TickAsync();

        tick.Skipped.Should().BeTrue();
        h.Mastodon.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task TwoReplicasTickingTogether_FetchEachThreadOnce()
    {
        await using var h = await HarnessAsync();
        var first = await h.SeedPostAsync(HoursAgo(1));
        var second = await h.SeedPostAsync(HoursAgo(2));
        var gated = new GatedHandler(_ => SocialFetchHarness.MastodonAnswer());
        h.UseMastodon(gated);
        var a = h.NewReplica();
        var b = h.NewReplica();

        var tickA = a.TickAsync();
        await gated.ArrivedOrTickEndedAsync(tickA); // A has claimed both threads and its first fetch is in flight
        var resultB = await b.TickAsync();
        gated.Release();
        var resultA = await tickA;

        resultA.Claimed.Should().Be(2);
        resultB.Claimed.Should().Be(0, "B finds both threads leased by A");
        gated.Requests.Should().Be(2, "each thread was fetched exactly once, by A");
        (await h.RepliesAsync(first, SocialPlatform.Mastodon)).Should().HaveCount(4);
        (await h.RepliesAsync(second, SocialPlatform.Mastodon)).Should().HaveCount(4);
    }

    [Fact]
    public async Task AnUnchangedSnapshot_UpdatesOnlyTheTimes_AndInvalidatesNothing()
    {
        await using var h = await HarnessAsync();
        var post = await h.SeedPostAsync(HoursAgo(1));
        var replica = h.NewReplica();
        await replica.TickAsync();
        var before = await h.ThreadAsync(post, SocialPlatform.Mastodon);
        var rowsBefore = await h.RepliesAsync(post, SocialPlatform.Mastodon);
        replica.Notifier.Notified.Should().HaveCount(1, "the first fetch changed the snapshot");

        h.Time.Advance(TimeSpan.FromMinutes(11));
        var tick = await replica.TickAsync();

        tick.Claimed.Should().Be(1);
        h.Mastodon.ContextRequests.Should().HaveCount(2);
        replica.Notifier.Notified.Should().HaveCount(1, "an unchanged snapshot notifies nobody");
        replica.Notifier.ClearedLocally.Should().HaveCount(1, "and clears nothing locally");
        var after = await h.ThreadAsync(post, SocialPlatform.Mastodon);
        after.SnapshotHash.Should().Be(before.SnapshotHash);
        after.LastSuccessAt.Should().Be(h.Time.GetUtcNow().UtcDateTime);
        after.LastAttemptAt.Should().Be(h.Time.GetUtcNow().UtcDateTime);
        after.NextDueAt.Should().Be(h.Time.GetUtcNow().UtcDateTime.AddMinutes(10));
        after.LeaseUntil.Should().BeNull();
        (await h.RepliesAsync(post, SocialPlatform.Mastodon)).Should().BeEquivalentTo(rowsBefore, "the rows were not rewritten");
    }

    [Fact]
    public async Task AChangedSnapshot_ReplacesTheRows_AndInvalidatesTheDiscussionGroupOnly()
    {
        await using var h = await HarnessAsync();
        var post = await h.SeedPostAsync(HoursAgo(1));
        var replica = h.NewReplica();
        await replica.TickAsync();
        var hashBefore = (await h.ThreadAsync(post, SocialPlatform.Mastodon)).SnapshotHash;

        // Upstream: the first reply (and so its subtree) was deleted, and a new one arrived.
        var changed = new MastodonThreadBuilder()
            .Reply("110054956705310518", MastodonThreadBuilder.RootId)
            .Reply("999", MastodonThreadBuilder.RootId)
            .ToJson();
        h.Mastodon.Respond = _ => SocialFetchHarness.MastodonAnswer(changed);
        h.Time.Advance(TimeSpan.FromMinutes(11));
        await replica.TickAsync();

        (await h.RepliesAsync(post, SocialPlatform.Mastodon)).Select(r => r.ExternalId)
            .Should().BeEquivalentTo(["110054956705310518", "999"], "what was deleted upstream is deleted here");
        (await h.ThreadAsync(post, SocialPlatform.Mastodon)).SnapshotHash.Should().NotBe(hashBefore);
        replica.Notifier.Notified.Should().Equal(CacheGroup.Discussion, CacheGroup.Discussion);
        replica.Notifier.ClearedLocally.Should().Equal(CacheGroup.Discussion, CacheGroup.Discussion);
    }

    [Fact]
    public async Task AFailure_KeepsTheSnapshot_RecordsTheError_AndInvalidatesNothing()
    {
        await using var h = await HarnessAsync();
        var post = await h.SeedPostAsync(HoursAgo(1));
        var replica = h.NewReplica();
        await replica.TickAsync();
        var before = await h.ThreadAsync(post, SocialPlatform.Mastodon);
        var rowsBefore = await h.RepliesAsync(post, SocialPlatform.Mastodon);

        h.Mastodon.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        h.Time.Advance(TimeSpan.FromMinutes(11));
        await replica.TickAsync();

        var after = await h.ThreadAsync(post, SocialPlatform.Mastodon);
        (await h.RepliesAsync(post, SocialPlatform.Mastodon)).Should().BeEquivalentTo(rowsBefore, "a failed fetch keeps the last snapshot");
        after.SnapshotHash.Should().Be(before.SnapshotHash);
        after.LastSuccessAt.Should().Be(before.LastSuccessAt, "it did not succeed");
        after.LastAttemptAt.Should().Be(h.Time.GetUtcNow().UtcDateTime);
        after.LastError.Should().Contain("HTTP 500").And.HaveLength(after.LastError!.Length);
        after.LastError!.Length.Should().BeLessThanOrEqualTo(500);
        after.LastError.Should().NotContain(" at Croft", "no stack trace");
        after.NextDueAt.Should().Be(h.Time.GetUtcNow().UtcDateTime.AddMinutes(10), "a failure is retried at the normal interval, not faster");
        after.LeaseUntil.Should().BeNull();
        replica.Notifier.Notified.Should().HaveCount(1, "only the first, successful fetch notified");
    }

    [Fact]
    public async Task AFailureOfOneThread_DoesNotStopTheOthers()
    {
        await using var h = await HarnessAsync();
        var broken = await h.SeedPostAsync(HoursAgo(2), mastodonStatusId: "111");
        var fine = await h.SeedPostAsync(HoursAgo(1), mastodonStatusId: "222");
        h.Mastodon.Respond = request => request.RequestUri!.AbsolutePath.Contains("/111/", StringComparison.Ordinal)
            ? throw new InvalidOperationException("unexpected fault with secret text")
            : SocialFetchHarness.MastodonAnswerFor(request);

        await h.NewReplica().TickAsync();

        var thread = await h.ThreadAsync(broken, SocialPlatform.Mastodon);
        thread.LastError.Should().NotBeNull().And.NotContain("secret text", "an unexpected fault is recorded by kind only");
        thread.LeaseUntil.Should().BeNull();
        (await h.RepliesAsync(fine, SocialPlatform.Mastodon)).Should().HaveCount(4);
    }

    [Fact]
    public async Task A429WithRetryAfter_ReleasesThePlatformsClaims_AndStopsAskingIt()
    {
        await using var h = await HarnessAsync();
        var posts = new List<Guid>();
        for (var i = 0; i < 3; i++)
            posts.Add(await h.SeedPostAsync(HoursAgo(1 + i), mastodonStatusId: $"10{i}"));
        var blueskyPost = await h.SeedPostAsync(HoursAgo(1), mastodonStatusId: null, blueskyUri: BlueskyThreadBuilder.RootUri);
        h.Mastodon.Respond = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return response;
        };
        var replica = h.NewReplica();

        await replica.TickAsync();

        h.Mastodon.Requests.Should().ContainSingle("after the 429 the tick moves on without asking Mastodon again");
        foreach (var post in posts)
        {
            var thread = await h.ThreadAsync(post, SocialPlatform.Mastodon);
            thread.LeaseUntil.Should().BeNull("the claim is released");
            thread.NextDueAt.Should().Be(Start.UtcDateTime.AddSeconds(120), "and due again when Retry-After says");
            thread.SnapshotHash.Should().BeNull();
        }

        (await h.RepliesAsync(blueskyPost, SocialPlatform.Bluesky)).Should().NotBeEmpty("Bluesky is not rate limited");
        replica.Notifier.Notified.Should().HaveCount(1, "only the Bluesky thread changed");
    }

    [Fact]
    public async Task MastodonsNoRateLimitLeft_StoresThatAnswer_AndReleasesTheRestUntilTheReset()
    {
        await using var h = await HarnessAsync();
        var first = await h.SeedPostAsync(HoursAgo(3), mastodonStatusId: "100");
        var second = await h.SeedPostAsync(HoursAgo(2), mastodonStatusId: "200");
        var third = await h.SeedPostAsync(HoursAgo(1), mastodonStatusId: "300");
        h.Mastodon.Respond = request =>
        {
            var response = SocialFetchHarness.MastodonAnswerFor(request, remaining: 0);
            response.Headers.Add("X-RateLimit-Reset", "2026-10-21T12:05:00.000000Z");
            return response;
        };

        await h.NewReplica().TickAsync();

        h.Mastodon.ContextRequests.Should().ContainSingle();
        var stored = (await h.RepliesAsync(first, SocialPlatform.Mastodon)).Count
            + (await h.RepliesAsync(second, SocialPlatform.Mastodon)).Count
            + (await h.RepliesAsync(third, SocialPlatform.Mastodon)).Count;
        stored.Should().Be(4, "one answer was good and stored");
        var released = new List<SocialThread>();
        foreach (var post in new[] { first, second, third })
        {
            var thread = await h.ThreadAsync(post, SocialPlatform.Mastodon);
            thread.LeaseUntil.Should().BeNull();
            if (thread.SnapshotHash is null)
                released.Add(thread);
        }

        released.Should().HaveCount(2);
        released.Should().OnlyContain(t => t.NextDueAt == new DateTime(2026, 10, 21, 12, 5, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task ABlockedReply_IsNotStored_NorAreTheAnswersBelowIt()
    {
        await using var h = await HarnessAsync();
        var post = await h.SeedPostAsync(HoursAgo(1));
        await AddBlockAsync(h, SocialPlatform.Mastodon, SocialBlockKind.Reply, "110054956705310518");
        // A Bluesky block with a Mastodon-looking id must change nothing on Mastodon.
        await AddBlockAsync(h, SocialPlatform.Bluesky, SocialBlockKind.Reply, "110054913016854354");

        await h.NewReplica().TickAsync();

        (await h.RepliesAsync(post, SocialPlatform.Mastodon)).Select(r => r.ExternalId).Should().Equal("110054913016854354");
    }

    [Fact]
    public async Task ABlockedAccount_IsNotStored_NorAreTheAnswersBelowItsReplies()
    {
        await using var h = await HarnessAsync();
        var post = await h.SeedPostAsync(HoursAgo(1));
        await AddBlockAsync(h, SocialPlatform.Mastodon, SocialBlockKind.Account, "109405514975902875"); // wrote 110054985368879806

        await h.NewReplica().TickAsync();

        (await h.RepliesAsync(post, SocialPlatform.Mastodon)).Select(r => r.ExternalId)
            .Should().Equal("110054913016854354", "110054956705310518");
    }

    private static async Task AddBlockAsync(SocialFetchHarness h, SocialPlatform platform, SocialBlockKind kind, string externalId)
    {
        await using var db = h.Database.CreateContext();
        db.SocialBlocks.Add(new SocialBlock { Platform = platform, Kind = kind, ExternalId = externalId, CreatedBy = "admin", CreatedAt = Start.UtcDateTime });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task AResultForALinkThatChangedMeanwhile_IsDiscarded()
    {
        await using var h = await HarnessAsync();
        var post = await h.SeedPostAsync(HoursAgo(1));
        var gated = new GatedHandler(_ => SocialFetchHarness.MastodonAnswer());
        h.UseMastodon(gated);
        var tick = h.NewReplica().TickAsync();
        await gated.ArrivedOrTickEndedAsync(tick);

        await using (var db = h.Database.CreateContext())
        {
            var linked = await db.Posts.SingleAsync(p => p.Id == post);
            linked.MastodonStatusId = "777";
            var thread = await db.SocialThreads.SingleAsync(t => t.PostId == post);
            thread.NextDueAt = Start.UtcDateTime;
            thread.LeaseUntil = null;
            await db.SaveChangesAsync();
        }

        gated.Release();
        await tick;

        (await h.RepliesAsync(post, SocialPlatform.Mastodon)).Should().BeEmpty("the replies belong to the old link");
        (await h.ThreadAsync(post, SocialPlatform.Mastodon)).SnapshotHash.Should().BeNull();
    }

    [Fact]
    public async Task APostDeletedMeanwhile_IsNotAnError()
    {
        await using var h = await HarnessAsync();
        var post = await h.SeedPostAsync(HoursAgo(1));
        var gated = new GatedHandler(_ => SocialFetchHarness.MastodonAnswer());
        h.UseMastodon(gated);
        var tick = h.NewReplica().TickAsync();
        await gated.ArrivedOrTickEndedAsync(tick);
        await using (var db = h.Database.CreateContext())
            await db.Posts.Where(p => p.Id == post).ExecuteDeleteAsync();

        gated.Release();
        var result = await tick;

        result.Claimed.Should().Be(1);
        await using var check = h.Database.CreateContext();
        (await check.SocialReplies.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ATickClaimsAtMostTwentyThreads_TheRestWaitForTheNextTick()
    {
        await using var h = await HarnessAsync();
        for (var i = 0; i < 25; i++)
            await h.SeedPostAsync(HoursAgo(1 + i / 100.0), mastodonStatusId: $"{1000 + i}");

        var first = await h.NewReplica().TickAsync();
        var second = await h.NewReplica().TickAsync();

        first.Claimed.Should().Be(SocialFetchHostedService.MaxClaimsPerTick).And.Be(20);
        second.Claimed.Should().Be(5, "the others were due all along; they are taken in the next tick");
        h.Mastodon.ContextRequests.Should().HaveCount(25);
    }

    [Fact]
    public async Task TheAvatarStep_GetsTheDistinctSourceUrls_ItsHashesAreStored_AndTheSweepRunsAfterEachSuccess()
    {
        await using var h = await HarnessAsync();
        var post = await h.SeedPostAsync(HoursAgo(1));
        var avatars = new SpyAvatars(url => url.EndsWith("/avatar/ba8a214b", StringComparison.Ordinal) ? new string('b', 64) : null);

        var replica = h.NewReplica(avatars);
        await replica.TickAsync();
        h.Time.Advance(TimeSpan.FromMinutes(11));
        await replica.TickAsync();

        avatars.Resolved.Should().HaveCount(2, "one resolve per fetched thread");
        avatars.Resolved[0].Should().OnlyHaveUniqueItems("each distinct source URL is asked for once");
        avatars.Resolved[0].Should().Contain("https://social.example/avatar/ba8a214b");
        var rows = await h.RepliesAsync(post, SocialPlatform.Mastodon);
        rows.Where(r => r.AvatarHash is not null).Should().NotBeEmpty().And.OnlyContain(r => r.AvatarHash == new string('b', 64));
        rows.Where(r => r.AvatarHash is null).Should().NotBeEmpty("a reply whose avatar failed shows the fallback");
        avatars.Sweeps.Should().Be(2, "the unreferenced avatars are swept after every successful refresh, changed or not");
    }

    private sealed class SpyAvatars(Func<string, string?> hashOf) : ISocialAvatars
    {
        public List<string[]> Resolved { get; } = [];

        public int Sweeps { get; private set; }

        public Task<IReadOnlyDictionary<string, string>> ResolveAsync(BlogDbContext db, IReadOnlyCollection<string> sourceUrls, CancellationToken ct)
        {
            Resolved.Add([.. sourceUrls]);
            return Task.FromResult<IReadOnlyDictionary<string, string>>(
                sourceUrls.Select(u => (Url: u, Hash: hashOf(u))).Where(x => x.Hash is not null).ToDictionary(x => x.Url, x => x.Hash!));
        }

        public Task SweepAsync(BlogDbContext db, CancellationToken ct)
        {
            Sweeps++;
            return Task.CompletedTask;
        }
    }
}
