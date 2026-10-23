using System.Net;
using AwesomeAssertions;
using Croft.Api.Services;
using Croft.Api.Social;
using Croft.Api.Tests.Fixtures;
using Croft.Api.Tests.Fixtures.Social;
using Croft.Shared.Contracts.Social;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace Croft.Api.Tests.Social;

/// <summary>Brief 12a item 4, end to end: a fetch resolves avatars, a bad avatar never fails the thread, and an avatar nobody uses is swept after a refresh.</summary>
public class SocialAvatarFetchTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private const string AvatarOfAccountA = "https://social.example/avatar/ba8a214b"; // replies 913, 956 and 006 of the recording
    private const string AvatarOfAccountB = "https://social.example/avatar/3fe4bb86"; // reply 985

    private static readonly DateTimeOffset Start = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private sealed class Rig(SocialFetchHarness harness, StubHandler avatarHandler, string storagePath, SocialAvatarService service) : IAsyncDisposable
    {
        public SocialFetchHarness Harness => harness;

        public StubHandler AvatarHandler => avatarHandler;

        public string StoragePath => storagePath;

        public SocialAvatarService Service => service;

        public async ValueTask DisposeAsync()
        {
            await harness.DisposeAsync();
            if (Directory.Exists(storagePath))
                Directory.Delete(storagePath, recursive: true);
        }
    }

    private async Task<Rig> RigAsync(Func<HttpRequestMessage, HttpResponseMessage> avatarAnswer)
    {
        var harness = await SocialFetchHarness.CreateAsync(fixture, Start);
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        var handler = new StubHandler(avatarAnswer);
        var service = new SocialAvatarService(new StubClientFactory(handler), new LocalFileStorage(storagePath), harness.Time, NullLogger<SocialAvatarService>.Instance);
        harness.Avatars = service;
        return new Rig(harness, handler, storagePath, service);
    }

    private static HttpResponseMessage Png() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(TestImages.Stripes(SKEncodedImageFormat.Png)) };

    [Fact]
    public async Task TheRepliesOfAnAccount_GetItsAvatarHash_AndAFailedAvatarLeavesItsRepliesWithTheFallback()
    {
        await using var rig = await RigAsync(request => request.RequestUri!.ToString() == AvatarOfAccountA ? Png() : new HttpResponseMessage(HttpStatusCode.NotFound));
        var post = await rig.Harness.SeedPostAsync(Start.UtcDateTime.AddHours(-1));

        await rig.Harness.NewReplica().TickAsync();

        var replies = await rig.Harness.RepliesAsync(post, SocialPlatform.Mastodon);
        replies.Should().HaveCount(4, "an avatar that failed does not fail the thread");
        var withAvatar = replies.Where(r => r.AvatarHash is not null).Select(r => r.ExternalId);
        withAvatar.Should().BeEquivalentTo(["110054913016854354", "110054956705310518", "110055006448893422"]);
        replies.Single(r => r.ExternalId == "110054985368879806").AvatarHash.Should().BeNull();
        await using var db = rig.Harness.Database.CreateContext();
        (await db.SocialAvatars.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData("oversized")]
    [InlineData("not an image")]
    [InlineData("claims 10000x10000")]
    public async Task ABadAvatar_LeavesAvatarHashNull_AndTheThreadIsStillStored(string kind)
    {
        await using var rig = await RigAsync(_ => kind switch
        {
            "oversized" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[(int)SocialHttp.AvatarLimit + 1]) },
            "not an image" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>not an image</html>") },
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(TestImages.PngClaiming(10000, 10000)) },
        });
        var post = await rig.Harness.SeedPostAsync(Start.UtcDateTime.AddHours(-1));

        await rig.Harness.NewReplica().TickAsync();

        var replies = await rig.Harness.RepliesAsync(post, SocialPlatform.Mastodon);
        replies.Should().HaveCount(4);
        replies.Should().OnlyContain(r => r.AvatarHash == null);
        var thread = await rig.Harness.ThreadAsync(post, SocialPlatform.Mastodon);
        thread.LastSuccessAt.Should().Be(Start.UtcDateTime);
        thread.LastError.Should().BeNull();
    }

    [Fact]
    public async Task AnUnchangedAvatarUrl_IsNotDownloadedAgainOnTheNextRefresh()
    {
        // Each URL has an image of its own (two URLs that show one image are one avatar, and the second URL is asked for again: see the service tests).
        await using var rig = await RigAsync(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(TestImages.Stripes(SKEncodedImageFormat.Png, sideStripe: request.RequestUri!.ToString() == AvatarOfAccountA ? 50 : 30, middle: request.RequestUri.ToString() == AvatarOfAccountA ? 100 : 40)),
        });
        await rig.Harness.SeedPostAsync(Start.UtcDateTime.AddHours(-1));
        var replica = rig.Harness.NewReplica();
        await replica.TickAsync();
        var downloads = rig.AvatarHandler.Requests.Count;

        rig.Harness.Time.Advance(TimeSpan.FromMinutes(11));
        await replica.TickAsync();

        downloads.Should().Be(2, "one download per distinct avatar URL of the recording");
        rig.AvatarHandler.Requests.Should().HaveCount(2, "the second refresh found both URLs in the table");
    }

    [Fact]
    public async Task AnAvatarNoStoredReplyUsesAnyMore_IsDeletedAfterTheNextRefresh()
    {
        await using var rig = await RigAsync(request => request.RequestUri!.ToString() == AvatarOfAccountA ? Png() : new HttpResponseMessage(HttpStatusCode.NotFound));
        var post = await rig.Harness.SeedPostAsync(Start.UtcDateTime.AddHours(-1));
        var replica = rig.Harness.NewReplica();
        await replica.TickAsync();
        Directory.GetFiles(rig.StoragePath, "*.webp", SearchOption.AllDirectories).Should().ContainSingle();

        // Upstream: every reply of account A is deleted; only account B's reply stays (as an answer to the post itself).
        var remaining = new MastodonThreadBuilder().Reply("110054985368879806", MastodonThreadBuilder.RootId, s => ((System.Text.Json.Nodes.JsonObject)s["account"]!)["id"] = "109405514975902875").ToJson();
        rig.Harness.Mastodon.Respond = _ => SocialFetchHarness.MastodonAnswer(remaining);
        rig.Harness.Time.Advance(TimeSpan.FromHours(2));
        await replica.TickAsync();

        (await rig.Harness.RepliesAsync(post, SocialPlatform.Mastodon)).Select(r => r.ExternalId).Should().Equal("110054985368879806");
        await using var db = rig.Harness.Database.CreateContext();
        (await db.SocialAvatars.CountAsync()).Should().Be(0, "no stored reply references the avatar of account A any more");
        Directory.GetFiles(rig.StoragePath, "*.webp", SearchOption.AllDirectories).Should().BeEmpty("the file went with the row");
    }
}
