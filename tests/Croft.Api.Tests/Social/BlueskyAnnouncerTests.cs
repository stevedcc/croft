using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Croft.Api.Social;
using Croft.Api.Tests.Fixtures.Social;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Croft.Api.Tests.Social;

/// <summary>
/// Brief 12d item 4: posting to Bluesky (createSession, uploadBlob for the card image, createRecord with the address as a link facet, the hashtags as tag
/// facets and an external-embed card), the look for an earlier send, and the outcomes. Stub PDS only; the answers are the fixtures.
/// </summary>
public class BlueskyAnnouncerTests
{
    private const string Did = SocialTestSettings.Did;
    private const string AppPassword = "test-pass-77c1";
    private const string Jwt = "test-jwt-9d2e.eyJ0ZXN0IjoxfQ.sig";
    private const string PostUrl = "https://blog.example/post/load-testing-the-homelab";
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 8, 0, 0, TimeSpan.Zero);

    private readonly Fixtures.FakeTimeProvider _time = new(Now);
    private readonly SocialOptions _settings = new()
    {
        Enabled = true,
        BaseUrl = "https://blog.example",
        AnnouncementLanguage = "en",
        Bluesky = new BlueskySocialOptions { Did = Did, AppPassword = AppPassword, Service = "https://pds.example" },
    };

    private BlueskyAnnouncer Announcer(HttpMessageHandler handler) =>
        new(new StubClientFactory(handler), Options.Create(_settings), _time);

    private static AnnouncementMessage Message(string text = $"Load testing the homelab\n\n{PostUrl}", byte[]? banner = null, string? subtitle = "What a Raspberry Pi cluster can take") =>
        new(Guid.NewGuid(), text, PostUrl, "Load testing the homelab", subtitle, banner is null ? null : _ => Task.FromResult<byte[]?>(banner));

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed record Seen(HttpRequestMessage Request, byte[] Body)
    {
        public string Text => Encoding.UTF8.GetString(Body);

        public JsonDocument Json => JsonDocument.Parse(Body);
    }

    /// <summary>A PDS that answers the three calls like the real one; <paramref name="respond"/> overrides an answer by the XRPC method name.</summary>
    private static (StubHandler Handler, List<Seen> Seen) Pds(Func<string, HttpResponseMessage?>? respond = null, string sessionHandle = "handle.invalid")
    {
        List<Seen> seen = [];
        var handler = new StubHandler(request =>
        {
            seen.Add(new Seen(request, request.Content is null ? [] : request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()));
            var method = request.RequestUri!.AbsolutePath.Split('/')[^1];
            return respond?.Invoke(method) ?? method switch
            {
                "com.atproto.server.createSession" => Json($$"""{"accessJwt":"{{Jwt}}","refreshJwt":"test-refresh-5b","handle":"{{sessionHandle}}","did":"{{Did}}"}"""),
                "com.atproto.repo.uploadBlob" => Json("""{"blob":{"$type":"blob","ref":{"$link":"bafkreihdwdcefgh4dqkjv67uzcmw7ojee6xedzdetojuzjevtenxquvyku"},"mimeType":"image/jpeg","size":12345}}"""),
                "com.atproto.repo.createRecord" => Json(SocialFixtures.Read("bluesky-create-record.json")),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        });
        return (handler, seen);
    }

    [Fact]
    public async Task Send_MakesTheThreeCalls_InOrder_WithoutABanner_OnlyTwo()
    {
        var (handler, seen) = Pds();

        var outcome = await Announcer(handler).SendAsync(Message(), CancellationToken.None);

        seen.Select(s => s.Request.RequestUri!.ToString()).Should().Equal(
            "https://pds.example/xrpc/com.atproto.server.createSession",
            "https://pds.example/xrpc/com.atproto.repo.createRecord");
        outcome.Should().Be(new AnnounceOutcome.Posted(
            $"at://{Did}/app.bsky.feed.post/3m5aaaaaaaaaa", $"https://bsky.app/profile/{Did}/post/3m5aaaaaaaaaa"));
    }

    [Fact]
    public async Task Send_WithAHandleInTheSessionAnswer_LinksByHandle_AndKeepsTheDidUriAsTheRemoteId()
    {
        var (handler, _) = Pds(sessionHandle: "owner.example");

        var outcome = await Announcer(handler).SendAsync(Message(), CancellationToken.None);

        outcome.Should().Be(new AnnounceOutcome.Posted(
            $"at://{Did}/app.bsky.feed.post/3m5aaaaaaaaaa", "https://bsky.app/profile/owner.example/post/3m5aaaaaaaaaa"));
    }

    [Fact]
    public async Task Send_WithAnInvalidHandle_LinksByDid()
    {
        var (handler, _) = Pds(sessionHandle: "handle.invalid");

        var outcome = await Announcer(handler).SendAsync(Message(), CancellationToken.None);

        outcome.Should().Be(new AnnounceOutcome.Posted(
            $"at://{Did}/app.bsky.feed.post/3m5aaaaaaaaaa", $"https://bsky.app/profile/{Did}/post/3m5aaaaaaaaaa"));
    }

    [Fact]
    public async Task Send_TheSessionRequest_CarriesTheDidAndThePassword_AndNoAuthorization()
    {
        var (handler, seen) = Pds();

        await Announcer(handler).SendAsync(Message(), CancellationToken.None);

        var session = seen[0];
        session.Request.Method.Should().Be(HttpMethod.Post);
        session.Request.Headers.Authorization.Should().BeNull();
        using var json = session.Json;
        json.RootElement.EnumerateObject().Select(p => (p.Name, p.Value.GetString())).Should().BeEquivalentTo([("identifier", Did), ("password", AppPassword)]);
    }

    [Fact]
    public async Task Send_TheRecord_HasTheTextTheLanguageTheLinkFacetAndTheCard()
    {
        var (handler, seen) = Pds();
        // ü is 2 bytes and 😀 is 4: the link starts at byte 26, not at character 22.
        var text = $"Grüße 😀 vom Cluster\n\n{PostUrl}";

        await Announcer(handler).SendAsync(Message(text), CancellationToken.None);

        var create = seen[1];
        create.Request.Headers.Authorization!.ToString().Should().Be($"Bearer {Jwt}");
        using var json = create.Json;
        var root = json.RootElement;
        root.GetProperty("repo").GetString().Should().Be(Did);
        root.GetProperty("collection").GetString().Should().Be("app.bsky.feed.post");
        var record = root.GetProperty("record");
        record.GetProperty("$type").GetString().Should().Be("app.bsky.feed.post");
        record.GetProperty("text").GetString().Should().Be(text);
        record.GetProperty("createdAt").GetString().Should().Be("2026-10-23T08:00:00.000Z");
        record.GetProperty("langs").EnumerateArray().Select(l => l.GetString()).Should().Equal("en");

        var facet = record.GetProperty("facets").EnumerateArray().Should().ContainSingle().Subject;
        facet.GetProperty("index").GetProperty("byteStart").GetInt32().Should().Be(26);
        facet.GetProperty("index").GetProperty("byteEnd").GetInt32().Should().Be(26 + PostUrl.Length);
        var feature = facet.GetProperty("features").EnumerateArray().Single();
        feature.GetProperty("$type").GetString().Should().Be("app.bsky.richtext.facet#link");
        feature.GetProperty("uri").GetString().Should().Be(PostUrl);

        var embed = record.GetProperty("embed");
        embed.GetProperty("$type").GetString().Should().Be("app.bsky.embed.external");
        var external = embed.GetProperty("external");
        external.GetProperty("uri").GetString().Should().Be(PostUrl);
        external.GetProperty("title").GetString().Should().Be("Load testing the homelab");
        external.GetProperty("description").GetString().Should().Be("What a Raspberry Pi cluster can take");
        external.TryGetProperty("thumb", out _).Should().BeFalse("there is no banner");
    }

    [Fact]
    public async Task Send_TheHashtagsInTheText_GetTagFacets_BesideTheLinkFacet_InOrder()
    {
        var (handler, seen) = Pds();
        var text = $"Grüße 😀 Load testing #homelab and #dotnet.\n\n{PostUrl}";

        await Announcer(handler).SendAsync(Message(text), CancellationToken.None);

        using var json = seen[1].Json;
        var facets = json.RootElement.GetProperty("record").GetProperty("facets").EnumerateArray().ToList();
        facets.Select(f => f.GetProperty("index").GetProperty("byteStart").GetInt32()).Should().Equal([26, 39, 49], "the facets are ordered by start; the address starts after \"#dotnet.\" and the blank line");
        var features = facets.Select(f => f.GetProperty("features").EnumerateArray().Single()).ToList();
        features.Select(f => f.GetProperty("$type").GetString()).Should().Equal("app.bsky.richtext.facet#tag", "app.bsky.richtext.facet#tag", "app.bsky.richtext.facet#link");
        features.Take(2).Select(f => f.GetProperty("tag").GetString()).Should().Equal("homelab", "dotnet");
    }

    [Fact]
    public async Task Send_WithABlankLanguage_SendsNoLangs()
    {
        _settings.AnnouncementLanguage = "";
        var (handler, seen) = Pds();

        await Announcer(handler).SendAsync(Message(), CancellationToken.None);

        using var json = seen[1].Json;
        json.RootElement.GetProperty("record").TryGetProperty("langs", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Send_WithoutASubtitle_TheCardDescriptionIsEmpty()
    {
        var (handler, seen) = Pds();

        await Announcer(handler).SendAsync(Message(subtitle: null), CancellationToken.None);

        using var json = seen[1].Json;
        json.RootElement.GetProperty("record").GetProperty("embed").GetProperty("external").GetProperty("description").GetString().Should().BeEmpty();
    }

    [Fact]
    public async Task Send_ABigPngBanner_IsFittedAndUploaded_AsAJpegUnderTheLimit_AndBecomesTheThumb()
    {
        var (handler, seen) = Pds();
        var banner = TestImages.Gradient(SKEncodedImageFormat.Png, 3000, 2000);

        await Announcer(handler).SendAsync(Message(banner: banner), CancellationToken.None);

        seen.Select(s => s.Request.RequestUri!.AbsolutePath.Split('/')[^1]).Should().Equal(
            "com.atproto.server.createSession", "com.atproto.repo.uploadBlob", "com.atproto.repo.createRecord");
        var upload = seen[1];
        upload.Request.Headers.Authorization!.ToString().Should().Be($"Bearer {Jwt}");
        upload.Request.Content!.Headers.ContentType!.MediaType.Should().Be("image/jpeg");
        upload.Body.Length.Should().BeLessThanOrEqualTo(950_000);
        var (format, width, height) = TestImages.Inspect(upload.Body);
        format.Should().Be(SKEncodedImageFormat.Jpeg);
        Math.Max(width, height).Should().BeLessThanOrEqualTo(1200);

        using var json = seen[2].Json;
        var thumb = json.RootElement.GetProperty("record").GetProperty("embed").GetProperty("external").GetProperty("thumb");
        thumb.GetProperty("$type").GetString().Should().Be("blob");
        thumb.GetProperty("mimeType").GetString().Should().Be("image/jpeg");
        thumb.GetProperty("size").GetInt32().Should().Be(12345);
    }

    [Fact]
    public async Task Send_ACorruptBanner_GivesACardWithoutAThumb_AndNoUpload()
    {
        var (handler, seen) = Pds();
        var png = TestImages.Gradient(SKEncodedImageFormat.Png, 300, 300);

        var outcome = await Announcer(handler).SendAsync(Message(banner: png[..(png.Length / 3)]), CancellationToken.None);

        outcome.Should().BeOfType<AnnounceOutcome.Posted>();
        seen.Select(s => s.Request.RequestUri!.AbsolutePath.Split('/')[^1]).Should().Equal("com.atproto.server.createSession", "com.atproto.repo.createRecord");
        using var json = seen[1].Json;
        json.RootElement.GetProperty("record").GetProperty("embed").GetProperty("external").TryGetProperty("thumb", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Send_ABannerThatCannotBeRead_GivesACardWithoutAThumb()
    {
        var (handler, seen) = Pds();
        var message = Message() with { LoadBanner = _ => Task.FromResult<byte[]?>(null) };

        (await Announcer(handler).SendAsync(message, CancellationToken.None)).Should().BeOfType<AnnounceOutcome.Posted>();
        seen.Should().HaveCount(2);
    }

    [Fact]
    public async Task Send_ARefusedBannerUpload_FailsAtOnce_AndCreateRecordIsNeverCalled()
    {
        var (handler, seen) = Pds(method => method == "com.atproto.repo.uploadBlob"
            ? Json("""{"error":"BlobTooLarge","message":"too big"}""", HttpStatusCode.BadRequest)
            : null);
        var banner = TestImages.Gradient(SKEncodedImageFormat.Png, 300, 300);

        var outcome = await Announcer(handler).SendAsync(Message(banner: banner), CancellationToken.None);

        outcome.Should().Be(new AnnounceOutcome.Failed("Bluesky refused the image upload (HTTP 400): BlobTooLarge"));
        seen.Select(s => s.Request.RequestUri!.AbsolutePath.Split('/')[^1]).Should().Equal("com.atproto.server.createSession", "com.atproto.repo.uploadBlob");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task Send_ARefusedSession_FailsAtOnce_AndNothingElseIsCalled(HttpStatusCode status)
    {
        var (handler, seen) = Pds(method => method == "com.atproto.server.createSession"
            ? Json($$"""{"error":"AuthenticationRequired","message":"Invalid identifier or password {{AppPassword}}"}""", status)
            : null);

        var outcome = await Announcer(handler).SendAsync(Message(), CancellationToken.None);

        outcome.Should().Be(new AnnounceOutcome.Failed($"Bluesky refused the sign-in (HTTP {(int)status}): AuthenticationRequired"));
        seen.Should().ContainSingle();
    }

    [Fact]
    public async Task Send_NoOutcomeCarriesAPasswordOrAToken()
    {
        var (refusedPost, _) = Pds(method => method == "com.atproto.repo.createRecord" ? Json($$"""{"error":"InvalidRequest {{Jwt}} and {{AppPassword}}"}""", HttpStatusCode.BadRequest) : null);

        var outcome = await Announcer(refusedPost).SendAsync(Message(), CancellationToken.None);

        var failed = outcome.Should().BeOfType<AnnounceOutcome.Failed>().Subject;
        failed.Reason.Should().StartWith("Bluesky refused the post (HTTP 400): InvalidRequest").And.NotContain(Jwt).And.NotContain(AppPassword);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task Send_CreateRecordTrouble_Retries(HttpStatusCode status)
    {
        var (handler, _) = Pds(method => method == "com.atproto.repo.createRecord" ? new HttpResponseMessage(status) : null);

        (await Announcer(handler).SendAsync(Message(), CancellationToken.None)).Should().Be(new AnnounceOutcome.Retry($"Bluesky answered HTTP {(int)status}", null));
    }

    [Fact]
    public async Task Send_ASessionTrouble_Retries()
    {
        var (handler, _) = Pds(method => method == "com.atproto.server.createSession" ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : null);

        (await Announcer(handler).SendAsync(Message(), CancellationToken.None)).Should().BeOfType<AnnounceOutcome.Retry>();
    }

    [Fact]
    public async Task Send_ARateLimit_RetriesAtTheResetTimeInUnixSeconds()
    {
        var reset = Now.AddMinutes(25);
        var (handler, _) = Pds(method =>
        {
            if (method != "com.atproto.repo.createRecord")
                return null;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") };
            response.Headers.Add("ratelimit-reset", reset.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
            return response;
        });

        (await Announcer(handler).SendAsync(Message(), CancellationToken.None))
            .Should().Be(new AnnounceOutcome.Retry("Bluesky is rate limiting (HTTP 429)", reset.UtcDateTime));
    }

    [Fact]
    public async Task Send_RetryAfterWinsOverTheResetHeader()
    {
        var (handler, _) = Pds(method =>
        {
            if (method != "com.atproto.repo.createRecord")
                return null;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") };
            response.Headers.Add("Retry-After", "120");
            response.Headers.Add("ratelimit-reset", Now.AddMinutes(50).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
            return response;
        });

        (await Announcer(handler).SendAsync(Message(), CancellationToken.None))
            .Should().Be(new AnnounceOutcome.Retry("Bluesky is rate limiting (HTTP 429)", Now.AddMinutes(2).UtcDateTime));
    }

    [Fact]
    public async Task Send_ATimeoutAndANetworkError_Retry()
    {
        (await Announcer(new ThrowingHandler(new TaskCanceledException("timed out"))).SendAsync(Message(), CancellationToken.None))
            .Should().Be(new AnnounceOutcome.Retry("Bluesky timed out", null));
        (await Announcer(new ThrowingHandler(new HttpRequestException("reset"))).SendAsync(Message(), CancellationToken.None))
            .Should().Be(new AnnounceOutcome.Retry("Bluesky could not be reached (network error)", null));
    }

    [Fact]
    public async Task Send_ASuccessWithoutAUri_Retries_SoTheCheckSettlesIt()
    {
        var (handler, _) = Pds(method => method == "com.atproto.repo.createRecord" ? Json("{}") : null);

        (await Announcer(handler).SendAsync(Message(), CancellationToken.None)).Should().BeOfType<AnnounceOutcome.Retry>();
    }

    // --- the look for an earlier send ---

    private static StubHandler Feed(Func<HttpResponseMessage>? answer = null) => new(_ => answer?.Invoke() ?? Json(SocialFixtures.Read("bluesky-author-feed.json")));

    [Fact]
    public async Task Check_ReadsTheAuthorFeed_OnThePublicAppView_WithoutCredentials()
    {
        var handler = Feed();

        await Announcer(handler).FindEarlierAsync(Message(), CancellationToken.None);

        handler.Requests.Should().ContainSingle().Which.ToString().Should().Be(
            $"https://public.api.bsky.app/xrpc/app.bsky.feed.getAuthorFeed?actor={Uri.EscapeDataString(Did)}&limit=30&filter=posts_no_replies");
        handler.Messages.Single().Headers.Authorization.Should().BeNull();
    }

    /// <summary>The author feed as Bluesky answers for an account whose handle does not resolve (the DID form stays).</summary>
    private static StubHandler FeedWithoutAHandle() => Feed(() => Json(SocialFixtures.Read("bluesky-author-feed.json").Replace("\"handle\": \"owner.example\"", "\"handle\": \"handle.invalid\"", StringComparison.Ordinal)));

    [Fact]
    public async Task Check_FindsAPostByItsCardUri()
    {
        (await Announcer(FeedWithoutAHandle()).FindEarlierAsync(Message(), CancellationToken.None))
            .Should().Be(new AnnounceOutcome.Posted($"at://{Did}/app.bsky.feed.post/3m5aaaaaaaaaa", $"https://bsky.app/profile/{Did}/post/3m5aaaaaaaaaa"));
    }

    [Fact]
    public async Task Check_UsesTheAuthorsHandleOfTheFeedItem_ForTheLink()
    {
        (await Announcer(Feed()).FindEarlierAsync(Message(), CancellationToken.None))
            .Should().Be(new AnnounceOutcome.Posted($"at://{Did}/app.bsky.feed.post/3m5aaaaaaaaaa", "https://bsky.app/profile/owner.example/post/3m5aaaaaaaaaa"));
    }

    [Fact]
    public async Task Check_FindsAPostByItsLinkFacet()
    {
        var message = Message() with { PostUrl = "https://blog.example/post/second-post" };

        (await Announcer(FeedWithoutAHandle()).FindEarlierAsync(message, CancellationToken.None))
            .Should().Be(new AnnounceOutcome.Posted($"at://{Did}/app.bsky.feed.post/3m5bbbbbbbbbb", $"https://bsky.app/profile/{Did}/post/3m5bbbbbbbbbb"));
    }

    [Fact]
    public async Task Check_WhenNoPostLinksToTheAddress_ReportsNotFound_AndAPrefixIsNoMatch()
    {
        (await Announcer(Feed()).FindEarlierAsync(Message() with { PostUrl = "https://blog.example/post/another" }, CancellationToken.None)).Should().Be(new AnnounceOutcome.NotFound());
        (await Announcer(Feed()).FindEarlierAsync(Message() with { PostUrl = "https://blog.example/post/second" }, CancellationToken.None)).Should().Be(new AnnounceOutcome.NotFound());
    }

    [Fact]
    public async Task Check_Trouble_RetriesInsteadOfSendingBlind()
    {
        (await Announcer(Feed(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))).FindEarlierAsync(Message(), CancellationToken.None))
            .Should().Be(new AnnounceOutcome.Retry("Bluesky answered HTTP 503", null));
        (await Announcer(new ThrowingHandler(new HttpRequestException("reset"))).FindEarlierAsync(Message(), CancellationToken.None)).Should().BeOfType<AnnounceOutcome.Retry>();
    }
}
