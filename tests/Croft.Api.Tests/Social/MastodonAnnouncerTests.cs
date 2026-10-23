using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Croft.Api.Social;
using Croft.Api.Tests.Fixtures.Social;
using Microsoft.Extensions.Options;

namespace Croft.Api.Tests.Social;

/// <summary>
/// Brief 12d item 3: posting to Mastodon (<c>POST /api/v1/statuses</c> with a bearer token and an Idempotency-Key of the row id), the look for an earlier
/// send, and the outcomes: posted, retry (with the rate limit's reset), failed for good. Stub server only; the answers are the fixtures.
/// </summary>
public class MastodonAnnouncerTests
{
    private const string Token = "test-token-0f3a";
    private const string PostUrl = "https://blog.example/post/load-testing-the-homelab";
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 8, 0, 0, TimeSpan.Zero);

    private readonly Fixtures.FakeTimeProvider _time = new(Now);
    private readonly SocialOptions _settings = new()
    {
        Enabled = true,
        BaseUrl = "https://blog.example",
        AnnouncementLanguage = "en",
        Mastodon = new MastodonSocialOptions { Server = "mastodon.example", Account = "owner", AccessToken = Token },
    };

    private MastodonAnnouncer Announcer(HttpMessageHandler handler) =>
        new(new StubClientFactory(handler), Options.Create(_settings), _time);

    private static AnnouncementMessage Message(Guid? rowId = null, string text = "Load testing the homelab\n\nhttps://blog.example/post/load-testing-the-homelab") =>
        new(rowId ?? Guid.NewGuid(), text, PostUrl, "Load testing the homelab", "What a Raspberry Pi cluster can take", null);

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>A server that answers the create with the fixture and records the request as the stub saw it (the body is read while the request is alive).</summary>
    private static (StubHandler Handler, List<(HttpRequestMessage Request, string Body)> Seen) Server(Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        List<(HttpRequestMessage, string)> seen = [];
        var handler = new StubHandler(request =>
        {
            seen.Add((request, request.Content is null ? "" : request.Content.ReadAsStringAsync().GetAwaiter().GetResult()));
            return respond?.Invoke(request) ?? Json(SocialFixtures.Read("mastodon-status-created.json"), HttpStatusCode.OK);
        });
        return (handler, seen);
    }

    [Fact]
    public async Task Send_PostsThePublicStatus_WithTheTokenAndTheIdempotencyKey()
    {
        var (handler, seen) = Server();
        var rowId = Guid.NewGuid();
        var message = Message(rowId);

        var outcome = await Announcer(handler).SendAsync(message, CancellationToken.None);

        outcome.Should().Be(new AnnounceOutcome.Posted("115000000000000042", "https://mastodon.example/@owner/115000000000000042"));
        var (request, body) = seen.Should().ContainSingle().Which;
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri.Should().Be(new Uri("https://mastodon.example/api/v1/statuses"));
        request.Headers.Authorization!.ToString().Should().Be($"Bearer {Token}");
        request.Headers.GetValues("Idempotency-Key").Should().Equal(rowId.ToString("D"));
        request.Content!.Headers.ContentType!.MediaType.Should().Be("application/json");
        using var json = JsonDocument.Parse(body);
        json.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["status", "visibility", "language"]);
        json.RootElement.GetProperty("status").GetString().Should().Be(message.Text);
        json.RootElement.GetProperty("visibility").GetString().Should().Be("public");
        json.RootElement.GetProperty("language").GetString().Should().Be("en");
    }

    [Fact]
    public async Task Send_WithABlankLanguage_SendsNone()
    {
        _settings.AnnouncementLanguage = "  ";
        var (handler, seen) = Server();

        await Announcer(handler).SendAsync(Message(), CancellationToken.None);

        using var json = JsonDocument.Parse(seen.Single().Body);
        json.RootElement.TryGetProperty("language", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Created)]
    public async Task Send_ASuccess_IsPosted(HttpStatusCode status)
    {
        var (handler, _) = Server(_ => Json(SocialFixtures.Read("mastodon-status-created.json"), status));

        var outcome = await Announcer(handler).SendAsync(Message(), CancellationToken.None);

        outcome.Should().BeOfType<AnnounceOutcome.Posted>();
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task Send_AnErrorThatCannotHeal_FailsAtOnce_WithTheServersErrorText(HttpStatusCode status)
    {
        var (handler, _) = Server(_ => Json("""{"error":"The access token is invalid"}""", status));

        var outcome = await Announcer(handler).SendAsync(Message(), CancellationToken.None);

        var failed = outcome.Should().BeOfType<AnnounceOutcome.Failed>().Subject;
        failed.Reason.Should().Be($"Mastodon refused the post (HTTP {(int)status}): The access token is invalid");
    }

    [Fact]
    public async Task Send_TheFailureReason_NeverCarriesTheToken_EvenIfTheServerEchoesIt()
    {
        var (handler, _) = Server(_ => Json($$"""{"error":"Bad token {{Token}} given"}""", HttpStatusCode.Unauthorized));

        var outcome = await Announcer(handler).SendAsync(Message(), CancellationToken.None);

        outcome.Should().BeOfType<AnnounceOutcome.Failed>().Which.Reason.Should().NotContain(Token).And.Contain("Bad token");
    }

    [Fact]
    public async Task Send_TheFailureReason_UsesOnlyTheErrorString_AndIsShort()
    {
        var (handler, _) = Server(_ => Json($$"""{"error":"{{new string('e', 900)}}","error_description":"secret detail","other":"x"}""", HttpStatusCode.UnprocessableEntity));

        var failed = (await Announcer(handler).SendAsync(Message(), CancellationToken.None)).Should().BeOfType<AnnounceOutcome.Failed>().Subject;

        failed.Reason.Should().NotContain("secret detail");
        failed.Reason.Length.Should().BeLessThan(300);

        var (html, _) = Server(_ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("<html>a whole page</html>") });
        (await Announcer(html).SendAsync(Message(), CancellationToken.None)).Should().Be(new AnnounceOutcome.Failed("Mastodon refused the post (HTTP 403)"));
    }

    [Fact]
    public async Task Send_ARateLimit_RetriesAtTheReset()
    {
        var reset = Now.AddMinutes(20);
        var (handler, _) = Server(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") };
            response.Headers.Add("X-RateLimit-Reset", reset.ToString("O"));
            return response;
        });

        var outcome = await Announcer(handler).SendAsync(Message(), CancellationToken.None);

        outcome.Should().Be(new AnnounceOutcome.Retry("Mastodon is rate limiting (HTTP 429)", reset.UtcDateTime));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task Send_ServerTroubleAndTimeouts_Retry(HttpStatusCode status)
    {
        var (handler, _) = Server(_ => new HttpResponseMessage(status));

        var outcome = await Announcer(handler).SendAsync(Message(), CancellationToken.None);

        outcome.Should().Be(new AnnounceOutcome.Retry($"Mastodon answered HTTP {(int)status}", null));
    }

    [Fact]
    public async Task Send_ATimeoutAndANetworkError_Retry()
    {
        (await Announcer(new ThrowingHandler(new TaskCanceledException("timed out"))).SendAsync(Message(), CancellationToken.None))
            .Should().Be(new AnnounceOutcome.Retry("Mastodon timed out", null));
        (await Announcer(new ThrowingHandler(new HttpRequestException($"connection to {Token} reset"))).SendAsync(Message(), CancellationToken.None))
            .Should().Be(new AnnounceOutcome.Retry("Mastodon could not be reached (network error)", null));
    }

    [Fact]
    public async Task Send_ASuccessWithoutAnId_Retries_SoTheCheckFindsWhatMayHaveBeenPosted()
    {
        var (handler, _) = Server(_ => Json("{}"));

        (await Announcer(handler).SendAsync(Message(), CancellationToken.None)).Should().BeOfType<AnnounceOutcome.Retry>();
    }

    [Fact]
    public async Task Send_WhenCancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var (handler, _) = Server();

        var act = () => Announcer(handler).SendAsync(Message(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // --- the look for an earlier send ---

    private static StubHandler CheckServer(Func<HttpRequestMessage, HttpResponseMessage>? statuses = null) => new(request =>
        request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/accounts/lookup" => Json("""{"id":"109000000000000001","acct":"owner"}"""),
            "/api/v1/accounts/109000000000000001/statuses" => statuses?.Invoke(request) ?? Json(SocialFixtures.Read("mastodon-account-statuses.json")),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

    [Fact]
    public async Task Check_LooksUpTheAccount_ThenReadsItsLatestStatuses_WithoutCredentials()
    {
        var handler = CheckServer();

        await Announcer(handler).FindEarlierAsync(Message(), CancellationToken.None);

        handler.Requests.Select(r => r.ToString()).Should().Equal(
            "https://mastodon.example/api/v1/accounts/lookup?acct=owner",
            "https://mastodon.example/api/v1/accounts/109000000000000001/statuses?limit=20&exclude_replies=true&exclude_reblogs=true");
        handler.Messages.Should().OnlyContain(m => m.Method == HttpMethod.Get && m.Headers.Authorization == null && !m.Headers.Contains("Idempotency-Key"));
    }

    [Fact]
    public async Task Check_FindsAStatusByItsContent()
    {
        var outcome = await Announcer(CheckServer()).FindEarlierAsync(Message(), CancellationToken.None);

        outcome.Should().Be(new AnnounceOutcome.Posted("115000000000000042", "https://mastodon.example/@owner/115000000000000042"));
    }

    [Fact]
    public async Task Check_FindsAStatusByItsCardUrl()
    {
        // Only the second status of the recording carries the address in its card, not in its text.
        var statuses = SocialFixtures.Load("mastodon-account-statuses.json").AsArray();
        statuses.RemoveAt(0);
        var handler = CheckServer(_ => Json(statuses.ToJsonString()));

        var outcome = await Announcer(handler).FindEarlierAsync(Message(), CancellationToken.None);

        outcome.Should().Be(new AnnounceOutcome.Posted("115000000000000041", "https://mastodon.example/@owner/115000000000000041"));
    }

    [Fact]
    public async Task Check_WhenNoStatusHasTheAddress_ReportsNotFound()
    {
        var other = new AnnouncementMessage(Guid.NewGuid(), "x", "https://blog.example/post/another-post", "t", null, null);

        (await Announcer(CheckServer()).FindEarlierAsync(other, CancellationToken.None)).Should().Be(new AnnounceOutcome.NotFound());
    }

    [Fact]
    public async Task Check_AddressThatIsOnlyAPrefixOfAnotherPostsAddress_IsNotAMatch()
    {
        // ".../load-testing-the-homelab-part-2" contains ".../load-testing-the-homelab" as text, but is another post.
        var statuses = SocialFixtures.Load("mastodon-account-statuses.json").AsArray();
        statuses[0]!["content"] = """<p><a href="https://blog.example/post/load-testing-the-homelab-part-2">x</a></p>""";
        statuses.RemoveAt(1);
        statuses.RemoveAt(1);

        (await Announcer(CheckServer(_ => Json(statuses.ToJsonString()))).FindEarlierAsync(Message(), CancellationToken.None))
            .Should().Be(new AnnounceOutcome.NotFound());
    }

    [Fact]
    public async Task Check_WithNoAccountConfigured_CannotLook_AndReportsNotFoundWithoutAsking()
    {
        _settings.Mastodon.Account = "";
        var handler = CheckServer();

        (await Announcer(handler).FindEarlierAsync(Message(), CancellationToken.None)).Should().Be(new AnnounceOutcome.NotFound());
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Check_Trouble_RetriesInsteadOfSendingBlind()
    {
        (await Announcer(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))).FindEarlierAsync(Message(), CancellationToken.None))
            .Should().Be(new AnnounceOutcome.Retry("Mastodon answered HTTP 503", null));
        (await Announcer(new ThrowingHandler(new HttpRequestException("reset"))).FindEarlierAsync(Message(), CancellationToken.None))
            .Should().BeOfType<AnnounceOutcome.Retry>();
        (await Announcer(CheckServer(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError))).FindEarlierAsync(Message(), CancellationToken.None))
            .Should().BeOfType<AnnounceOutcome.Retry>();
    }

    [Fact]
    public async Task Check_AnUnknownAccount_FailsForGood()
    {
        var outcome = await Announcer(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))).FindEarlierAsync(Message(), CancellationToken.None);

        outcome.Should().Be(new AnnounceOutcome.Failed("Mastodon refused the account lookup (HTTP 404)"));
    }
}
