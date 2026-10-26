using System.Net;
using AwesomeAssertions;
using Croft.Api.Social;
using Croft.Api.Tests.Fixtures;
using Croft.Api.Tests.Fixtures.Social;
using Croft.Shared.Contracts.Social;
using Microsoft.Extensions.Options;

namespace Croft.Api.Tests.Social;

/// <summary>Brief 12a item 3: asking Mastodon for a status's context, and what each answer comes to (a stub; no real server is called).</summary>
public class MastodonThreadSourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly SocialClaim Claim = new(Guid.NewGuid(), SocialPlatform.Mastodon, SocialFixtures.MastodonRootId, Now.UtcDateTime);

    private static MastodonThreadSource Source(HttpMessageHandler handler) => new(
        new StubClientFactory(handler),
        Options.Create(new SocialOptions { Mastodon = new MastodonSocialOptions { Server = "mastodon.example", Account = "owner" } }),
        new FakeTimeProvider(Now));

    private static Task<SocialFetchOutcome> FetchAsync(HttpMessageHandler handler, SocialClaim? claim = null) =>
        Source(handler).FetchAsync(claim ?? Claim, SocialBlockSet.Empty, CancellationToken.None);

    [Fact]
    public async Task AsksTheConfiguredServerForTheContextOfTheStatus()
    {
        var handler = new StubHandler(_ => SocialFetchHarness.MastodonAnswer());

        await FetchAsync(handler);

        // Brief 17e adds a second request (the status itself, for its counts); the context request is still the first, unchanged.
        handler.Requests[0].ToString().Should().Be("https://mastodon.example/api/v1/statuses/110054900866478823/context");
        handler.Messages[0].Method.Should().Be(HttpMethod.Get);
        handler.Messages[0].Headers.Authorization.Should().BeNull("12a sends no credentials");
    }

    private static StubHandler Routed(Func<HttpRequestMessage, HttpResponseMessage> status) =>
        new(request => request.RequestUri!.AbsolutePath.EndsWith("/context", StringComparison.Ordinal) ? SocialFetchHarness.MastodonAnswer() : status(request));

    [Fact]
    public async Task AfterTheContext_AsksForTheStatusItself_AndYieldsItsCounts()
    {
        var handler = new StubHandler(request => SocialFetchHarness.MastodonAnswerFor(request));

        var outcome = await FetchAsync(handler);

        handler.Requests.Select(r => r.ToString()).Should().Equal(
            "https://mastodon.example/api/v1/statuses/110054900866478823/context",
            "https://mastodon.example/api/v1/statuses/110054900866478823");
        handler.Messages[1].Headers.Authorization.Should().BeNull();
        // The recorded status: favourites_count 0, reblogs_count 1, replies_count 2.
        outcome.Should().BeOfType<SocialFetchOutcome.Fetched>().Which.Counts.Should().Be(new SocialCounts(Likes: 0, Reposts: 1, Replies: 2));
    }

    [Fact]
    public async Task AFailedStatusRequest_StillYieldsTheReplies_WithNoCounts()
    {
        var outcome = await FetchAsync(Routed(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var fetched = outcome.Should().BeOfType<SocialFetchOutcome.Fetched>().Subject;
        fetched.Replies.Should().HaveCount(4);
        fetched.Counts.Should().BeNull();
    }

    [Fact]
    public async Task ANetworkErrorOnTheStatusRequest_StillYieldsTheReplies_WithNoCounts()
    {
        var calls = 0;
        var handler = new StubHandler(_ => ++calls == 1 ? SocialFetchHarness.MastodonAnswer() : throw new HttpRequestException("reset"));

        var outcome = await FetchAsync(handler);

        outcome.Should().BeOfType<SocialFetchOutcome.Fetched>().Which.Should().Match<SocialFetchOutcome.Fetched>(f => f.Replies.Count == 4 && f.Counts == null);
    }

    [Fact]
    public async Task AStatusRequestThatIsRateLimited_HasNoCounts_ButItsRetryAfterMarksThePlatformBusy()
    {
        var outcome = await FetchAsync(Routed(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return response;
        }));

        var fetched = outcome.Should().BeOfType<SocialFetchOutcome.Fetched>().Subject;
        fetched.Replies.Should().HaveCount(4);
        fetched.Counts.Should().BeNull();
        fetched.PlatformBusyUntil.Should().Be(Now.UtcDateTime.AddSeconds(120));
    }

    [Fact]
    public async Task AStatusAnswerWithoutCounts_YieldsNullCounts_NotZero()
    {
        var outcome = await FetchAsync(Routed(_ => SocialFetchHarness.MastodonAnswer("{\"id\":\"1\"}")));

        outcome.Should().BeOfType<SocialFetchOutcome.Fetched>().Which.Counts.Should().BeNull();
    }

    [Fact]
    public async Task ASuccess_YieldsTheFilteredReplies_AndNoBusyMarkWhileRateLimitRemains()
    {
        var outcome = await FetchAsync(new StubHandler(_ => SocialFetchHarness.MastodonAnswer(remaining: 12)));

        var fetched = outcome.Should().BeOfType<SocialFetchOutcome.Fetched>().Subject;
        fetched.Replies.Should().HaveCount(4);
        fetched.PlatformBusyUntil.Should().BeNull();
    }

    [Fact]
    public async Task AnAnswerWithNoRateLimitLeft_IsStillStored_ButMarksThePlatformBusyUntilTheReset()
    {
        var handler = new StubHandler(_ =>
        {
            var response = SocialFetchHarness.MastodonAnswer(remaining: 0);
            response.Headers.Add("X-RateLimit-Reset", "2026-10-21T12:05:00.123456Z");
            return response;
        });

        var outcome = await FetchAsync(handler);

        var fetched = outcome.Should().BeOfType<SocialFetchOutcome.Fetched>().Subject;
        fetched.Replies.Should().HaveCount(4, "the answer that used up the limit is a good answer");
        fetched.PlatformBusyUntil.Should().Be(new DateTime(2026, 10, 21, 12, 5, 0, DateTimeKind.Utc).AddTicks(1234560));
    }

    [Fact]
    public async Task NoRateLimitLeft_WithoutAResetHeader_BacksOffFiveMinutes()
    {
        var outcome = await FetchAsync(new StubHandler(_ => SocialFetchHarness.MastodonAnswer(remaining: 0)));

        outcome.Should().BeOfType<SocialFetchOutcome.Fetched>().Which.PlatformBusyUntil.Should().Be(Now.UtcDateTime.AddMinutes(5));
    }

    [Fact]
    public async Task A429_WithRetryAfterSeconds_IsRateLimitedUntilThen()
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return response;
        });

        (await FetchAsync(handler)).Should().Be(new SocialFetchOutcome.RateLimited(Now.UtcDateTime.AddSeconds(120)));
    }

    [Fact]
    public async Task A429_WithRetryAfterAsADate_IsRateLimitedUntilThatDate()
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(Now.AddMinutes(7));
            return response;
        });

        (await FetchAsync(handler)).Should().Be(new SocialFetchOutcome.RateLimited(Now.UtcDateTime.AddMinutes(7)));
    }

    [Fact]
    public async Task A429_WithOnlyAResetHeader_IsRateLimitedUntilTheReset()
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.Add("X-RateLimit-Reset", "2026-10-21T12:03:00.000000Z");
            return response;
        });

        (await FetchAsync(handler)).Should().Be(new SocialFetchOutcome.RateLimited(new DateTime(2026, 10, 21, 12, 3, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public async Task A429_WithNoHint_BacksOffFiveMinutes()
    {
        (await FetchAsync(StubHandler.Status(HttpStatusCode.TooManyRequests))).Should()
            .Be(new SocialFetchOutcome.RateLimited(Now.UtcDateTime.AddMinutes(5)));
    }

    [Fact]
    public async Task A429_WithAnAbsurdRetryAfter_IsCappedAtOneHour()
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromDays(30));
            return response;
        });

        (await FetchAsync(handler)).Should().Be(new SocialFetchOutcome.RateLimited(Now.UtcDateTime.AddHours(1)));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "HTTP 500")]
    [InlineData(HttpStatusCode.NotFound, "HTTP 404")]
    [InlineData(HttpStatusCode.Forbidden, "HTTP 403")]
    public async Task AnotherStatus_Fails_NamingOnlyTheStatus(HttpStatusCode status, string expected)
    {
        var outcome = await FetchAsync(StubHandler.Status(status));

        outcome.Should().BeOfType<SocialFetchOutcome.Failed>().Which.Error.Should().Contain(expected).And.NotContain("mastodon.example");
    }

    [Fact]
    public async Task ANetworkError_Fails_WithoutItsMessage()
    {
        var outcome = await FetchAsync(new ThrowingHandler(new HttpRequestException("connection refused by 203.0.113.9")));

        outcome.Should().BeOfType<SocialFetchOutcome.Failed>().Which.Error.Should().Contain("network").And.NotContain("203.0.113.9");
    }

    [Fact]
    public async Task ATimeout_Fails()
    {
        var outcome = await FetchAsync(new ThrowingHandler(new TaskCanceledException("timed out")));

        outcome.Should().BeOfType<SocialFetchOutcome.Failed>().Which.Error.Should().Contain("timed out");
    }

    [Fact]
    public async Task AnOversizedAnswer_Fails()
    {
        var outcome = await FetchAsync(StubHandler.Json(new string('x', (int)SocialHttp.ThreadLimit + 10)));

        outcome.Should().BeOfType<SocialFetchOutcome.Failed>().Which.Error.Should().Contain("too large");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    public async Task AnAnswerThatIsNotAContext_Fails(string body)
    {
        var outcome = await FetchAsync(StubHandler.Json(body));

        outcome.Should().BeOfType<SocialFetchOutcome.Failed>().Which.Error.Should().Contain("unexpected");
    }

    [Fact]
    public async Task ARemoteIdThatIsNotDigits_IsNeverPutInAUrl()
    {
        var handler = new StubHandler(_ => SocialFetchHarness.MastodonAnswer());

        var outcome = await FetchAsync(handler, Claim with { RemoteId = "1/../../admin" });

        outcome.Should().BeOfType<SocialFetchOutcome.Failed>();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task TheBlocklist_IsAppliedToTheReplies()
    {
        var blocks = new SocialBlockSet(["110054913016854354"], []);

        var outcome = await Source(new StubHandler(_ => SocialFetchHarness.MastodonAnswer())).FetchAsync(Claim, blocks, CancellationToken.None);

        outcome.Should().BeOfType<SocialFetchOutcome.Fetched>().Which.Replies.Select(r => r.ExternalId)
            .Should().NotContain("110054913016854354");
    }

    [Fact]
    public async Task ACancelledCaller_IsNotAFailureOfTheThread()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = () => Source(new ThrowingHandler(new OperationCanceledException(cancelled.Token))).FetchAsync(Claim, SocialBlockSet.Empty, cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
