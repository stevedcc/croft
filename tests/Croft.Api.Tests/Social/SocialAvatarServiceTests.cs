using System.Net;
using System.Security.Cryptography;
using AwesomeAssertions;
using Croft.Api.Models;
using Croft.Api.Services;
using Croft.Api.Social;
using Croft.Api.Tests.Fixtures;
using Croft.Api.Tests.Fixtures.Social;
using Croft.Shared.Contracts.Social;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Croft.Api.Tests.Social;

/// <summary>
/// Brief 12a item 4: an avatar is downloaded once, decoded, centre-cropped to a square and re-encoded to a 48x48 WebP (which strips metadata and
/// defuses a malicious image), stored under the SHA-256 of the WebP. A bad avatar never fails a thread: that reply shows the fallback.
/// </summary>
public class SocialAvatarServiceTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private const string Url = "https://avatars.example/img/one";

    private sealed class Rig : IAsyncDisposable
    {
        public required ScratchDatabase Database { get; init; }

        public required string StoragePath { get; init; }

        public required StubHandler Handler { get; init; }

        public required LogCollector Logs { get; init; }

        public required FakeTimeProvider Time { get; init; }

        public required SocialAvatarService Service { get; init; }

        public LocalFileStorage Storage { get; init; } = null!;

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            if (Directory.Exists(StoragePath))
                Directory.Delete(StoragePath, recursive: true);
        }

        public async Task<IReadOnlyDictionary<string, string>> ResolveAsync(params string[] urls)
        {
            await using var db = Database.CreateContext();
            return await Service.ResolveAsync(db, urls, CancellationToken.None);
        }

        public async Task SweepAsync()
        {
            await using var db = Database.CreateContext();
            await Service.SweepAsync(db, CancellationToken.None);
        }

        public async Task<List<SocialAvatar>> RowsAsync()
        {
            await using var db = Database.CreateContext();
            return await db.SocialAvatars.AsNoTracking().OrderBy(a => a.Hash).ToListAsync();
        }

        public byte[] FileOf(SocialAvatar avatar)
        {
            using var stream = Storage.OpenRead(avatar.StoragePath);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }
    }

    private async Task<Rig> RigAsync(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        var storage = new LocalFileStorage(storagePath);
        var handler = new StubHandler(respond);
        var logs = new LogCollector();
        var loggers = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(logs));
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));
        return new Rig
        {
            Database = await ScratchDatabase.CreateAsync(fixture.ConnectionString),
            StoragePath = storagePath,
            Storage = storage,
            Handler = handler,
            Logs = logs,
            Time = time,
            Service = new SocialAvatarService(new StubClientFactory(handler), storage, time, loggers.CreateLogger<SocialAvatarService>()),
        };
    }

    private static HttpResponseMessage Image(byte[] bytes, string contentType = "image/png") =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) { Headers = { { "Content-Type", contentType } } } };

    [Theory]
    [InlineData(SKEncodedImageFormat.Png)]
    [InlineData(SKEncodedImageFormat.Jpeg)]
    [InlineData(SKEncodedImageFormat.Webp)]
    public async Task APngJpegOrWebp_BecomesA48x48Webp_StoredUnderTheHashOfItsBytes(SKEncodedImageFormat format)
    {
        await using var rig = await RigAsync(_ => Image(TestImages.Stripes(format)));

        var hashes = await rig.ResolveAsync(Url);

        var hash = hashes[Url];
        hash.Should().MatchRegex("^[0-9a-f]{64}$");
        var row = (await rig.RowsAsync()).Should().ContainSingle().Subject;
        row.Hash.Should().Be(hash);
        row.SourceUrl.Should().Be(Url);
        row.FetchedAt.Should().Be(rig.Time.GetUtcNow().UtcDateTime);
        row.StoragePath.Should().StartWith("social-avatars/").And.EndWith(".webp");
        var file = rig.FileOf(row);
        TestImages.Inspect(file).Should().Be((SKEncodedImageFormat.Webp, 48, 48));
        Convert.ToHexStringLower(SHA256.HashData(file)).Should().Be(hash, "the key is the SHA-256 of the stored WebP bytes");
    }

    [Fact]
    public async Task AGif_UsesItsFirstFrame()
    {
        await using var rig = await RigAsync(_ => Image(TestImages.Gif, "image/gif"));

        var hashes = await rig.ResolveAsync(Url);

        var row = (await rig.RowsAsync()).Should().ContainSingle().Subject;
        hashes[Url].Should().Be(row.Hash);
        TestImages.Inspect(rig.FileOf(row)).Should().Be((SKEncodedImageFormat.Webp, 48, 48));
    }

    [Fact]
    public async Task ANonSquareImage_IsCroppedAroundItsCentre_NotSqueezed()
    {
        // 200 x 100: the middle 100 x 100 is green, the outer thirds red and blue.
        await using var rig = await RigAsync(_ => Image(TestImages.Stripes(SKEncodedImageFormat.Png, sideStripe: 50, middle: 100, height: 100)));

        await rig.ResolveAsync(Url);

        var file = rig.FileOf((await rig.RowsAsync()).Single());
        foreach (var (x, y) in new[] { (1, 24), (46, 24), (24, 1), (24, 46) })
        {
            var pixel = TestImages.PixelAt(file, x, y);
            (pixel.Green > 150 && pixel.Red < 100 && pixel.Blue < 100).Should().BeTrue($"the pixel at {x},{y} is green, so the sides were cropped away");
        }
    }

    [Fact]
    public async Task TheSameImage_GivesTheSameHash_AndTwoUrlsOfOneImageShareOneFile()
    {
        var png = TestImages.Stripes(SKEncodedImageFormat.Png);
        await using var rig = await RigAsync(_ => Image(png));

        var hashes = await rig.ResolveAsync("https://avatars.example/a", "https://avatars.example/b");

        hashes["https://avatars.example/a"].Should().Be(hashes["https://avatars.example/b"]);
        (await rig.RowsAsync()).Should().ContainSingle("one image is one avatar, however many URLs point to it");
        Directory.GetFiles(rig.StoragePath, "*.webp", SearchOption.AllDirectories).Should().ContainSingle();
    }

    [Fact]
    public async Task ADifferentImage_GivesADifferentHash()
    {
        var calls = 0;
        await using var rig = await RigAsync(_ => Image(++calls == 1
            ? TestImages.Stripes(SKEncodedImageFormat.Png)
            : TestImages.Stripes(SKEncodedImageFormat.Png, sideStripe: 30, middle: 40, height: 100)));

        var hashes = await rig.ResolveAsync("https://avatars.example/a", "https://avatars.example/b");

        hashes["https://avatars.example/a"].Should().NotBe(hashes["https://avatars.example/b"]);
    }

    [Fact]
    public async Task AKnownUrl_IsNeverDownloadedAgain()
    {
        await using var rig = await RigAsync(_ => Image(TestImages.Stripes(SKEncodedImageFormat.Png)));
        var first = await rig.ResolveAsync(Url);

        var second = await rig.ResolveAsync(Url);

        second[Url].Should().Be(first[Url]);
        rig.Handler.Requests.Should().ContainSingle("the second asking came from the table");
    }

    [Fact]
    public async Task EachDistinctUrlOfOneCall_IsDownloadedOnce()
    {
        await using var rig = await RigAsync(_ => Image(TestImages.Stripes(SKEncodedImageFormat.Png)));

        await rig.ResolveAsync(Url, Url, "https://avatars.example/img/two");

        rig.Handler.Requests.Select(u => u.ToString()).Should().BeEquivalentTo([Url, "https://avatars.example/img/two"]);
    }

    [Fact]
    public async Task TheDownload_IsAPlainGet_FlaggedSoItsUrlIsKeptOutOfTheTraces()
    {
        await using var rig = await RigAsync(_ => Image(TestImages.Stripes(SKEncodedImageFormat.Png)));

        await rig.ResolveAsync(Url);

        var request = rig.Handler.Messages.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Get);
        request.Options.TryGetValue(Croft.Shared.Telemetry.TelemetryRedaction.HostOnly, out var hostOnly).Should().BeTrue();
        hostOnly.Should().BeTrue("an avatar's path can carry a handle, so its spans export the host only");
    }

    [Fact]
    public async Task ABodyOverOneMegabyte_IsRefused_AndNeverStored()
    {
        await using var rig = await RigAsync(_ => Image(new byte[(int)SocialHttp.AvatarLimit + 1]));

        var hashes = await rig.ResolveAsync(Url);

        hashes.Should().BeEmpty();
        (await rig.RowsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ABodyThatIsNotAnImage_IsRefused()
    {
        await using var rig = await RigAsync(_ => Image("<html><script>alert(1)</script></html>"u8.ToArray(), "text/html"));

        (await rig.ResolveAsync(Url)).Should().BeEmpty();
        (await rig.RowsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task AnImageFormatOtherThanPngJpegWebpOrGif_IsRefused()
    {
        // A BMP header (BM + sizes) with a 1x1 pixel: Skia can decode BMP, but only the four formats are allowed.
        byte[] bmp =
        [
            0x42, 0x4D, 0x3A, 0, 0, 0, 0, 0, 0, 0, 0x36, 0, 0, 0, 0x28, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 24, 0, 0, 0, 0, 0, 4, 0, 0, 0,
            0x13, 0x0B, 0, 0, 0x13, 0x0B, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0, 0, 0,
        ];
        await using var rig = await RigAsync(_ => Image(bmp, "image/bmp"));

        (await rig.ResolveAsync(Url)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(10000, 10000)]
    [InlineData(4097, 10)]
    [InlineData(10, 4097)]
    public async Task AnImageClaimingMoreThan4096PixelsASide_IsRefusedBeforeItIsDecoded(int width, int height)
    {
        await using var rig = await RigAsync(_ => Image(TestImages.PngClaiming(width, height)));

        (await rig.ResolveAsync(Url)).Should().BeEmpty();
        rig.Logs.Entries.Should().Contain(e => e.Message.Contains($"{width}x{height}", StringComparison.Ordinal),
            "the refusal names the dimensions, which is how it is told apart from an undecodable file");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task AFailureStatus_LeavesTheAvatarOut(HttpStatusCode status)
    {
        await using var rig = await RigAsync(_ => new HttpResponseMessage(status));

        (await rig.ResolveAsync(Url)).Should().BeEmpty();
    }

    [Fact]
    public async Task ANetworkErrorOrATimeout_LeavesTheAvatarOut_WithoutThrowing()
    {
        await using var rig = await RigAsync(_ => throw new HttpRequestException("connection refused"));
        (await rig.ResolveAsync(Url)).Should().BeEmpty();

        rig.Handler.Respond = _ => throw new TaskCanceledException("timed out");
        (await rig.ResolveAsync("https://avatars.example/img/two")).Should().BeEmpty();
    }

    [Fact]
    public async Task AFailedAvatar_DoesNotStopTheOthersOfTheSameCall()
    {
        await using var rig = await RigAsync(request => request.RequestUri!.AbsolutePath.EndsWith("bad", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : Image(TestImages.Stripes(SKEncodedImageFormat.Png)));

        var hashes = await rig.ResolveAsync("https://avatars.example/bad", "https://avatars.example/good");

        hashes.Keys.Should().Equal("https://avatars.example/good");
    }

    [Theory]
    [InlineData("http://avatars.example/img/one")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not a url")]
    [InlineData("ftp://avatars.example/img/one")]
    public async Task OnlyAnHttpsUrl_IsEverRequested(string url)
    {
        await using var rig = await RigAsync(_ => Image(TestImages.Stripes(SKEncodedImageFormat.Png)));

        (await rig.ResolveAsync(url)).Should().BeEmpty();
        rig.Handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ACancelledCaller_IsNotAnAvatarFailure()
    {
        await using var rig = await RigAsync(_ => throw new OperationCanceledException());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await using var db = rig.Database.CreateContext();

        var act = () => rig.Service.ResolveAsync(db, [Url], cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task NoLogLine_CarriesTheAvatarUrl()
    {
        await using var rig = await RigAsync(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        await rig.ResolveAsync("https://avatars.example/accounts/some-handle/avatar.png");

        rig.Logs.Entries.Should().NotContain(e => e.Message.Contains("some-handle", StringComparison.Ordinal) || e.Message.Contains("avatars.example", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheSweep_DeletesAvatarsNoReplyReferences_RowAndFile_ButKeepsTheReferencedAndTheJustFetched()
    {
        await using var rig = await RigAsync(_ => Image(TestImages.Stripes(SKEncodedImageFormat.Png)));
        var referenced = await SeedAvatarAsync(rig, "referenced", ageHours: 5);
        var orphan = await SeedAvatarAsync(rig, "orphan", ageHours: 5);
        var fresh = await SeedAvatarAsync(rig, "fresh", ageHours: 0);
        await using (var db = rig.Database.CreateContext())
        {
            var post = new Post { Slug = "p", Title = "p", MarkdownContent = "x", Status = Croft.Shared.Enums.PostStatus.Published, AuthorId = "a", AuthorName = "A" };
            db.Posts.Add(post);
            db.SocialReplies.Add(new SocialReply
            {
                Post = post, Platform = SocialPlatform.Mastodon, ExternalId = "1", AuthorHandle = "h", AuthorDisplayName = "H", AuthorUrl = "https://x.example/",
                AuthorAccountId = "1", AvatarHash = referenced.Hash, Permalink = "https://x.example/1", ContentHtml = "<p>x</p>", CreatedAt = DateTime.UtcNow, FetchedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await rig.SweepAsync();

        (await rig.RowsAsync()).Select(a => a.Hash).Should().BeEquivalentTo([referenced.Hash, fresh.Hash],
            "the unreferenced avatar is gone; one fetched within the last hour may belong to a refresh that is still storing its replies");
        File.Exists(Path.Combine(rig.StoragePath, orphan.StoragePath)).Should().BeFalse("the file goes with the row");
        File.Exists(Path.Combine(rig.StoragePath, referenced.StoragePath)).Should().BeTrue();
        File.Exists(Path.Combine(rig.StoragePath, fresh.StoragePath)).Should().BeTrue();
    }

    [Fact]
    public async Task TheSweep_WithAMissingFile_StillDeletesTheRow()
    {
        await using var rig = await RigAsync(_ => Image(TestImages.Stripes(SKEncodedImageFormat.Png)));
        var orphan = await SeedAvatarAsync(rig, "orphan", ageHours: 5);
        File.Delete(Path.Combine(rig.StoragePath, orphan.StoragePath));

        await rig.SweepAsync();

        (await rig.RowsAsync()).Should().BeEmpty();
    }

    private static async Task<SocialAvatar> SeedAvatarAsync(Rig rig, string name, double ageHours)
    {
        await using var stream = new MemoryStream(TestImages.Stripes(SKEncodedImageFormat.Webp));
        var path = await rig.Storage.SaveAsync(stream, $"{name}.webp", "image/webp", "social-avatars");
        var avatar = new SocialAvatar
        {
            Hash = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name))),
            SourceUrl = $"https://avatars.example/{name}",
            StoragePath = path,
            FetchedAt = rig.Time.GetUtcNow().UtcDateTime.AddHours(-ageHours),
        };
        await using var db = rig.Database.CreateContext();
        db.SocialAvatars.Add(avatar);
        await db.SaveChangesAsync();
        return avatar;
    }
}
