using System.Net;
using AwesomeAssertions;
using Croft.Api.Data;
using Croft.Api.Services;
using Croft.Api.Social;
using Croft.Api.Tests.Fixtures;
using Croft.Api.Tests.Fixtures.Social;
using Croft.Shared.Contracts.Social;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using static Croft.Api.Tests.Fixtures.Social.DiscussionSeed;

namespace Croft.Api.Tests.Social;

/// <summary>Brief 12a item 5, blocking: a block removes a reply or an account's replies (with their subtrees) at once, on every post and every replica; unblocking brings them back on the next fetch.</summary>
public class DiscussionBlocksTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>, IAsyncLifetime
{
    // Blocks are global (not tied to a post), so each test gets a database of its own: every block and thread it sees is its own.
    private ScratchDatabase _database = null!;

    public async Task InitializeAsync() => _database = await ScratchDatabase.CreateAsync(fixture.ConnectionString);

    public async Task DisposeAsync() => await _database.DisposeAsync();

    private static readonly DateTimeOffset Start = new(Noon.AddHours(1));

    private static DateTime At(int minutes) => Noon.AddMinutes(minutes);

    private static CreateSocialBlockRequest Block(SocialPlatform platform, SocialBlockKind kind, string id, string? label = "Name (handle)", string? note = null) =>
        new(platform, kind, id, label, note);

    [Fact]
    public async Task CreatingABlock_Is201_WithWhoAndWhen_AndAListShowsItNewestFirst()
    {
        var time = new FakeTimeProvider(Start);
        await using var factory = SocialApiFactory.Create(_database.ConnectionString, time: time);
        var admin = factory.CreateClient().WithUser("admin-1", "Admin", "Ada Admin");

        var first = await admin.PostAsJsonAsync("/api/discussion/blocks", Block(SocialPlatform.Mastodon, SocialBlockKind.Reply, "r-1", "Reader (@reader)", "spam"));
        time.Advance(TimeSpan.FromMinutes(5));
        var second = await admin.PostAsJsonAsync("/api/discussion/blocks", Block(SocialPlatform.Bluesky, SocialBlockKind.Account, "did:plc:troll"));

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        first.Headers.Location.Should().NotBeNull();
        var dto = (await first.Content.ReadFromJsonAsync<SocialBlockDto>())!;
        dto.Should().BeEquivalentTo(new SocialBlockDto(dto.Id, SocialPlatform.Mastodon, SocialBlockKind.Reply, "r-1", "Reader (@reader)", "spam", "Ada Admin", new DateTimeOffset(Start.UtcDateTime)));
        second.StatusCode.Should().Be(HttpStatusCode.Created);
        var list = (await admin.GetFromJsonAsync<List<SocialBlockDto>>("/api/discussion/blocks"))!;
        list.Select(b => b.ExternalId).Should().Equal("did:plc:troll", "r-1");
    }

    [Fact]
    public async Task ADuplicate_ReturnsTheExistingBlock_With200()
    {
        await using var factory = SocialApiFactory.Create(_database.ConnectionString);
        var admin = factory.CreateClient().WithRole("Admin");
        var request = Block(SocialPlatform.Mastodon, SocialBlockKind.Account, "acct-dup");
        var first = (await (await admin.PostAsJsonAsync("/api/discussion/blocks", request)).Content.ReadFromJsonAsync<SocialBlockDto>())!;

        var again = await admin.PostAsJsonAsync("/api/discussion/blocks", request with { Label = "another label", Note = "another note" });

        again.StatusCode.Should().Be(HttpStatusCode.OK);
        (await again.Content.ReadFromJsonAsync<SocialBlockDto>())!.Should().BeEquivalentTo(first,
            options => options.Using<DateTimeOffset>(c => c.Subject.Should().BeCloseTo(c.Expectation, TimeSpan.FromMilliseconds(1))).WhenTypeIs<DateTimeOffset>(),
            "the existing block, untouched (its time as the database keeps it, to the microsecond)");
        (await admin.GetFromJsonAsync<List<SocialBlockDto>>("/api/discussion/blocks"))!.Count(b => b.ExternalId == "acct-dup").Should().Be(1);
    }

    [Fact]
    public async Task TheSameIdOnAnotherPlatformOrKind_IsAnotherBlock()
    {
        await using var factory = SocialApiFactory.Create(_database.ConnectionString);
        var admin = factory.CreateClient().WithRole("Admin");

        var statuses = new List<HttpStatusCode>();
        foreach (var (platform, kind) in new[] { (SocialPlatform.Mastodon, SocialBlockKind.Reply), (SocialPlatform.Bluesky, SocialBlockKind.Reply), (SocialPlatform.Mastodon, SocialBlockKind.Account) })
            statuses.Add((await admin.PostAsJsonAsync("/api/discussion/blocks", Block(platform, kind, "same-id"))).StatusCode);

        statuses.Should().OnlyContain(s => s == HttpStatusCode.Created);
    }

    [Fact]
    public async Task TwoBlocksAtOnce_LeaveOneRow_AndNeitherFails()
    {
        await using var factory = SocialApiFactory.Create(_database.ConnectionString);
        var admin = factory.CreateClient().WithRole("Admin");
        var request = Block(SocialPlatform.Mastodon, SocialBlockKind.Account, "acct-race");

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => admin.PostAsJsonAsync("/api/discussion/blocks", request)));

        responses.Select(r => r.StatusCode).Should().OnlyContain(s => s == HttpStatusCode.Created || s == HttpStatusCode.OK);
        responses.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);
        (await admin.GetFromJsonAsync<List<SocialBlockDto>>("/api/discussion/blocks"))!.Count(b => b.ExternalId == "acct-race").Should().Be(1);
    }

    public static TheoryData<string, SocialPlatform, SocialBlockKind, string?, string?> BadBlocks => new()
    {
        { "no id", SocialPlatform.Mastodon, SocialBlockKind.Reply, "", null },
        { "blank id", SocialPlatform.Mastodon, SocialBlockKind.Reply, "   ", null },
        { "too long an id", SocialPlatform.Mastodon, SocialBlockKind.Reply, new string('x', 513), null },
        { "unknown platform", (SocialPlatform)9, SocialBlockKind.Reply, "r-1", null },
        { "unknown kind", SocialPlatform.Mastodon, (SocialBlockKind)9, "r-1", null },
        { "too long a label", SocialPlatform.Mastodon, SocialBlockKind.Reply, "r-1", new string('x', 513) },
    };

    [Theory]
    [MemberData(nameof(BadBlocks))]
    public async Task ABadRequest_Is400_AndCreatesNothing(string why, SocialPlatform platform, SocialBlockKind kind, string? id, string? label)
    {
        await using var factory = SocialApiFactory.Create(_database.ConnectionString);
        var admin = factory.CreateClient().WithRole("Admin");
        var before = (await admin.GetFromJsonAsync<List<SocialBlockDto>>("/api/discussion/blocks"))!.Count;

        var response = await admin.PostAsJsonAsync("/api/discussion/blocks", new CreateSocialBlockRequest(platform, kind, id!, label, null));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, why);
        (await admin.GetFromJsonAsync<List<SocialBlockDto>>("/api/discussion/blocks"))!.Count.Should().Be(before);
    }

    [Fact]
    public async Task BlockingAReply_RemovesItAndItsSubtreeAtOnce_OnEveryPost_AndOnlyOnThatPlatform()
    {
        await using var factory = SocialApiFactory.Create(_database.ConnectionString, time: new FakeTimeProvider(Start));
        var admin = factory.CreateClient().WithRole("Admin");
        var one = await PostAsync(admin, "Post one");
        var two = await PostAsync(admin, "Post two");
        ReplySpec[] tree = [new("r-1", null, At(0)), new("r-1a", "r-1", At(1)), new("r-1b", "r-1a", At(2)), new("r-2", null, At(3))];
        await StoreAsync(factory, one.Id, SocialPlatform.Mastodon, Noon, tree);
        await StoreAsync(factory, two.Id, SocialPlatform.Mastodon, Noon, tree); // the same status linked from a second post
        await StoreAsync(factory, one.Id, SocialPlatform.Bluesky, Noon, new ReplySpec("r-1", null, At(0)));

        (await admin.PostAsJsonAsync("/api/discussion/blocks", Block(SocialPlatform.Mastodon, SocialBlockKind.Reply, "r-1"))).StatusCode.Should().Be(HttpStatusCode.Created);

        (await StoredIdsAsync(factory, one.Id, SocialPlatform.Mastodon)).Should().Equal("r-2");
        (await StoredIdsAsync(factory, two.Id, SocialPlatform.Mastodon)).Should().Equal("r-2");
        (await StoredIdsAsync(factory, one.Id, SocialPlatform.Bluesky)).Should().ContainSingle().Which.Should().Be("r-1", "a Mastodon block does not touch a Bluesky reply with the same id");
    }

    [Fact]
    public async Task BlockingAnAccount_RemovesItsRepliesAndTheirSubtrees_OnEveryPost()
    {
        await using var factory = SocialApiFactory.Create(_database.ConnectionString, time: new FakeTimeProvider(Start));
        var admin = factory.CreateClient().WithRole("Admin");
        var one = await PostAsync(admin, "Account post one");
        var two = await PostAsync(admin, "Account post two");
        await StoreAsync(factory, one.Id, SocialPlatform.Bluesky, Noon,
            new ReplySpec("a1", null, At(0), "did:plc:troll"), new ReplySpec("a1x", "a1", At(1), "did:plc:friend"),
            new ReplySpec("f1", null, At(2), "did:plc:friend"), new ReplySpec("a2", "f1", At(3), "did:plc:troll"), new ReplySpec("a2x", "a2", At(4), "did:plc:friend"));
        await StoreAsync(factory, two.Id, SocialPlatform.Bluesky, Noon, new ReplySpec("a3", null, At(0), "did:plc:troll"), new ReplySpec("f2", null, At(1), "did:plc:friend"));

        (await admin.PostAsJsonAsync("/api/discussion/blocks", Block(SocialPlatform.Bluesky, SocialBlockKind.Account, "did:plc:troll"))).EnsureSuccessStatusCode();

        (await StoredIdsAsync(factory, one.Id, SocialPlatform.Bluesky)).Should().Equal("f1");
        (await StoredIdsAsync(factory, two.Id, SocialPlatform.Bluesky)).Should().Equal("f2");
    }

    [Fact]
    public async Task AfterABlock_TheThreadsSnapshotHashDescribesWhatIsLeft_SoTheNextFetchChangesNothing()
    {
        await using var factory = SocialApiFactory.Create(_database.ConnectionString, time: new FakeTimeProvider(Start));
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await PostAsync(admin, "Hash after block");
        await StoreAsync(factory, post.Id, SocialPlatform.Mastodon, Noon, new ReplySpec("r-1", null, At(0)), new ReplySpec("r-2", null, At(1)));

        (await admin.PostAsJsonAsync("/api/discussion/blocks", Block(SocialPlatform.Mastodon, SocialBlockKind.Reply, "r-1"))).EnsureSuccessStatusCode();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        var thread = await db.SocialThreads.AsNoTracking().SingleAsync(t => t.PostId == post.Id && t.Platform == SocialPlatform.Mastodon);
        var rows = await db.SocialReplies.AsNoTracking().Where(r => r.PostId == post.Id && r.Platform == SocialPlatform.Mastodon).ToListAsync();
        thread.SnapshotHash.Should().Be(SocialSnapshot.Hash(rows));
    }

    [Fact]
    public async Task ABlockInvalidatesTheCachedDiscussion_OnThisReplicaAtOnce()
    {
        var counter = new SqlCommandCounter();
        await using var factory = SocialApiFactory.Create(_database.ConnectionString, time: new FakeTimeProvider(Start), commandInterceptor: counter);
        var admin = factory.CreateClient().WithRole("Admin");
        var anonymous = factory.CreateClient().AsAnonymous();
        var post = await PostAsync(admin, "Cached then blocked");
        await StoreAsync(factory, post.Id, SocialPlatform.Mastodon, Noon, new ReplySpec("r-1", null, At(0)), new ReplySpec("r-1a", "r-1", At(1)), new ReplySpec("r-2", null, At(2)));
        Shape((await ReadAsync(anonymous, post.Slug)).Replies).Should().Equal("r-1[r-1a]", "r-2");

        (await admin.PostAsJsonAsync("/api/discussion/blocks", Block(SocialPlatform.Mastodon, SocialBlockKind.Reply, "r-1"))).EnsureSuccessStatusCode();

        Shape((await ReadAsync(anonymous, post.Slug)).Replies).Should().Equal("r-2");
    }

    [Fact]
    public async Task ABlockThroughOneReplica_RemovesTheReplyFromTheNextReadOnAnother()
    {
        await using var a = ReplicaWithoutListener();
        await using var b = ReplicaWithListener();
        await WaitForListenerAsync(b);
        var adminA = a.CreateClient().WithRole("Admin");
        var anonymousB = b.CreateClient().AsAnonymous();
        var post = await PostAsync(adminA, $"Two replicas {Guid.NewGuid():N}");
        await StoreAsync(a, post.Id, SocialPlatform.Mastodon, DateTime.UtcNow, new ReplySpec("r-1", null, DateTime.UtcNow.AddMinutes(-3)), new ReplySpec("r-1a", "r-1", DateTime.UtcNow.AddMinutes(-2)), new ReplySpec("r-2", null, DateTime.UtcNow.AddMinutes(-1)));
        Shape((await ReadAsync(anonymousB, post.Slug)).Replies).Should().Equal("r-1[r-1a]", "r-2"); // cached on B

        (await adminA.PostAsJsonAsync("/api/discussion/blocks", Block(SocialPlatform.Mastodon, SocialBlockKind.Reply, "r-1"))).EnsureSuccessStatusCode();

        await WaitUntilAsync(async () => Shape((await ReadAsync(anonymousB, post.Slug)).Replies).SequenceEqual(["r-2"]),
            "B's cached discussion no longer shows the blocked reply and its subtree after A blocks it");
    }

    [Fact]
    public async Task Unblocking_Is204_MakesThatPlatformsThreadsDueNow_AndAnUnknownIdIs404()
    {
        var time = new FakeTimeProvider(Start);
        await using var factory = SocialApiFactory.Create(_database.ConnectionString, time: time);
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await PostAsync(admin, "Unblock me");
        await StoreAsync(factory, post.Id, SocialPlatform.Mastodon, Noon, new ReplySpec("r-1", null, At(0)));
        await StoreAsync(factory, post.Id, SocialPlatform.Bluesky, Noon, new ReplySpec("b-1", null, At(0)));
        var block = (await (await admin.PostAsJsonAsync("/api/discussion/blocks", Block(SocialPlatform.Mastodon, SocialBlockKind.Reply, "r-1"))).Content.ReadFromJsonAsync<SocialBlockDto>())!;
        time.Advance(TimeSpan.FromMinutes(7));

        var deleted = await admin.DeleteAsync($"/api/discussion/blocks/{block.Id}");

        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await admin.GetFromJsonAsync<List<SocialBlockDto>>("/api/discussion/blocks"))!.Should().NotContain(b => b.Id == block.Id);
        var threads = (await admin.GetFromJsonAsync<PostDiscussionAdminDto>($"/api/posts/{post.Id}/discussion/admin"))!.Threads;
        var now = new DateTimeOffset(Start.UtcDateTime.AddMinutes(7));
        threads.Single(t => t.Platform == SocialPlatform.Mastodon).NextDueAt.Should().Be(now, "the replies come back on the next fetch");
        threads.Single(t => t.Platform == SocialPlatform.Bluesky).NextDueAt.Should().NotBe(now, "Bluesky is not affected");
        (await admin.DeleteAsync($"/api/discussion/blocks/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ABlock_SurvivesThePostItWasMadeOn()
    {
        await using var factory = SocialApiFactory.Create(_database.ConnectionString);
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await PostAsync(admin, "Deleted after block");
        await StoreAsync(factory, post.Id, SocialPlatform.Mastodon, Noon, new ReplySpec("r-9", null, At(0)));
        var block = (await (await admin.PostAsJsonAsync("/api/discussion/blocks", Block(SocialPlatform.Mastodon, SocialBlockKind.Reply, "r-9"))).Content.ReadFromJsonAsync<SocialBlockDto>())!;

        (await admin.DeleteAsync($"/api/posts/{post.Id}")).EnsureSuccessStatusCode();

        (await admin.GetFromJsonAsync<List<SocialBlockDto>>("/api/discussion/blocks"))!.Should().Contain(b => b.Id == block.Id, "a block is not tied to a post");
    }

    private BlogApiFactory ReplicaWithoutListener() => Replica(withListener: false);

    private BlogApiFactory ReplicaWithListener() => Replica(withListener: true);

    private BlogApiFactory Replica(bool withListener) => SocialApiFactory.Create(_database.ConnectionString, configureServices: services =>
    {
        // The scheduler and the rest would run their own SQL on their own schedule; the cache listener is the one hosted service that must run.
        services.RemoveAll<IHostedService>();
        if (withListener)
            services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<CacheListenerService>());
    });

    private static async Task WaitForListenerAsync(BlogApiFactory factory)
    {
        var listener = factory.Services.GetRequiredService<CacheListenerService>();
        await WaitUntilAsync(() => Task.FromResult(listener.BackendPid is not null), "the cache listener of B is connected");
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        (await condition()).Should().BeTrue($"within 10 s: {what}");
    }
}
