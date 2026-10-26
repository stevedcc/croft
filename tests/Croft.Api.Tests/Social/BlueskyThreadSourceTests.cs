using System.Net;
using AwesomeAssertions;
using Croft.Api.Social;
using Croft.Api.Tests.Fixtures;
using Croft.Api.Tests.Fixtures.Social;
using Croft.Shared.Contracts.Social;

namespace Croft.Api.Tests.Social;

/// <summary>Brief 12a item 3: asking Bluesky's public service for a post thread, and what each answer comes to (a stub).</summary>
public class BlueskyThreadSourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly SocialClaim Claim = new(Guid.NewGuid(), SocialPlatform.Bluesky, BlueskyThreadBuilder.RootUri, Now.UtcDateTime);

    private static Task<SocialFetchOutcome> FetchAsync(HttpMessageHandler handler, SocialBlockSet? blocks = null) =>
        new BlueskyThreadSource(new StubClientFactory(handler), new FakeTimeProvider(Now))
            .FetchAsync(Claim, blocks ?? SocialBlockSet.Empty, CancellationToken.None);

    [Fact]
    public async Task AsksThePublicServiceForTheThread_WithDepth10AndNoParents()
    {
        var handler = new StubHandler(_ => SocialFetchHarness.BlueskyAnswer());

        await FetchAsync(handler);

        handler.Requests.Should().ContainSingle().Which.ToString().Should().Be(
            "https://public.api.bsky.app/xrpc/app.bsky.feed.getPostThread?uri="
            + Uri.EscapeDataString(BlueskyThreadBuilder.RootUri) + "&depth=10&parentHeight=0");
        handler.Messages[0].Headers.Authorization.Should().BeNull("the public service needs no credentials");
    }

    [Fact]
    public async Task ASuccess_YieldsTheFilteredReplies()
    {
        var outcome = await FetchAsync(new StubHandler(_ => SocialFetchHarness.BlueskyAnswer()));

        var fetched = outcome.Should().BeOfType<SocialFetchOutcome.Fetched>().Subject;
        fetched.Replies.Select(r => r.ExternalId).Should().Equal(
            "at://did:plc:alpha/app.bsky.feed.post/alpha", "at://did:plc:alpha1/app.bsky.feed.post/alpha1", "at://did:plc:beta/app.bsky.feed.post/beta");
        fetched.PlatformBusyUntil.Should().BeNull();
    }

    [Fact]
    public async Task ASuccess_CarriesTheRootsCounts()
    {
        var outcome = await FetchAsync(new StubHandler(_ => SocialFetchHarness.BlueskyAnswer(SocialFixtures.Read("bluesky-thread.json"))));

        outcome.Should().BeOfType<SocialFetchOutcome.Fetched>().Which.Counts.Should().Be(new SocialCounts(63724, 9545, 8675));
    }

    [Fact]
    public async Task TheBlocklist_IsAppliedToTheReplies()
    {
        var blocks = new SocialBlockSet([], ["did:plc:alpha"]);

        var outcome = await FetchAsync(new StubHandler(_ => SocialFetchHarness.BlueskyAnswer()), blocks);

        outcome.Should().BeOfType<SocialFetchOutcome.Fetched>().Which.Replies.Select(r => r.ExternalId)
            .Should().Equal("at://did:plc:beta/app.bsky.feed.post/beta");
    }

    [Fact]
    public async Task A429_WithRetryAfter_IsRateLimitedUntilThen()
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(90));
            return response;
        });

        (await FetchAsync(handler)).Should().Be(new SocialFetchOutcome.RateLimited(Now.UtcDateTime.AddSeconds(90)));
    }

    [Fact]
    public async Task A429_WithNoHint_BacksOffFiveMinutes()
    {
        (await FetchAsync(StubHandler.Status(HttpStatusCode.TooManyRequests))).Should()
            .Be(new SocialFetchOutcome.RateLimited(Now.UtcDateTime.AddMinutes(5)));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, "HTTP 502")]
    [InlineData(HttpStatusCode.BadRequest, "HTTP 400")]
    public async Task AnotherStatus_Fails_NamingOnlyTheStatus(HttpStatusCode status, string expected)
    {
        var outcome = await FetchAsync(StubHandler.Status(status));

        outcome.Should().BeOfType<SocialFetchOutcome.Failed>().Which.Error.Should().Contain(expected);
    }

    [Fact]
    public async Task ANetworkError_ATimeout_AnOversizedAnswer_AndAMalformedOne_AllFail()
    {
        (await FetchAsync(new ThrowingHandler(new HttpRequestException("connection refused")))).Should()
            .BeOfType<SocialFetchOutcome.Failed>().Which.Error.Should().Contain("network").And.NotContain("refused");
        (await FetchAsync(new ThrowingHandler(new TaskCanceledException()))).Should()
            .BeOfType<SocialFetchOutcome.Failed>().Which.Error.Should().Contain("timed out");
        (await FetchAsync(StubHandler.Json(new string('x', (int)SocialHttp.ThreadLimit + 10)))).Should()
            .BeOfType<SocialFetchOutcome.Failed>().Which.Error.Should().Contain("too large");
        (await FetchAsync(StubHandler.Json("not json"))).Should()
            .BeOfType<SocialFetchOutcome.Failed>().Which.Error.Should().Contain("unexpected");
    }

    [Fact]
    public async Task ARootThatWasDeleted_IsAnEmptyThread_NotAFailure()
    {
        var gone = new System.Text.Json.Nodes.JsonObject { ["thread"] = BlueskyThreadBuilder.NotFound(BlueskyThreadBuilder.RootUri) }.ToJsonString();

        var outcome = await FetchAsync(StubHandler.Json(gone));

        outcome.Should().BeOfType<SocialFetchOutcome.Fetched>().Which.Replies.Should().BeEmpty();
    }
}
