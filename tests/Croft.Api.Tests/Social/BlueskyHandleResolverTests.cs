using System.Net;
using AwesomeAssertions;
using Croft.Api.Errors;
using Croft.Api.Social;
using Croft.Api.Tests.Fixtures.Social;

namespace Croft.Api.Tests.Social;

/// <summary>Brief 12a item 1: the handle resolution at save, against a stub for Bluesky (no real call).</summary>
public class BlueskyHandleResolverTests
{
    [Fact]
    public async Task Resolves_AHandleToItsDid_AskingBlueskysPublicService()
    {
        var handler = StubHandler.Json("""{"did":"did:plc:resolved00000000000000"}""");
        var resolver = new BlueskyHandleResolver(new StubClientFactory(handler));

        var did = await resolver.ResolveDidAsync("owner.example");

        did.Should().Be("did:plc:resolved00000000000000");
        handler.Requests.Should().ContainSingle().Which.ToString()
            .Should().Be("https://bsky.social/xrpc/com.atproto.identity.resolveHandle?handle=owner.example");
    }

    [Fact]
    public async Task AHandleTheServiceDoesNotKnow_IsAValidationError_NamingTheField()
    {
        var handler = StubHandler.Json("""{"error":"InvalidRequest","message":"Unable to resolve handle"}""", HttpStatusCode.BadRequest);
        var resolver = new BlueskyHandleResolver(new StubClientFactory(handler));

        var act = () => resolver.ResolveDidAsync("nobody.example");

        (await act.Should().ThrowAsync<ValidationException>()).Which.Message.Should().Contain("BlueskyUrl").And.Contain("nobody.example");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task AServiceThatFails_IsUnavailable_NotAMistake(HttpStatusCode status)
    {
        var resolver = new BlueskyHandleResolver(new StubClientFactory(StubHandler.Status(status)));

        var act = () => resolver.ResolveDidAsync("owner.example");

        await act.Should().ThrowAsync<UpstreamUnavailableException>();
    }

    [Fact]
    public async Task ANetworkFailure_IsUnavailable()
    {
        var resolver = new BlueskyHandleResolver(new StubClientFactory(new ThrowingHandler(new HttpRequestException("connection refused"))));

        var act = () => resolver.ResolveDidAsync("owner.example");

        await act.Should().ThrowAsync<UpstreamUnavailableException>();
    }

    [Fact]
    public async Task ATimeout_IsUnavailable()
    {
        var resolver = new BlueskyHandleResolver(new StubClientFactory(new ThrowingHandler(new TaskCanceledException("timed out"))));

        var act = () => resolver.ResolveDidAsync("owner.example");

        await act.Should().ThrowAsync<UpstreamUnavailableException>();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"did":"owner.example"}""")]
    [InlineData("""{"did":null}""")]
    public async Task AnUnexpectedAnswer_IsUnavailable(string body)
    {
        var resolver = new BlueskyHandleResolver(new StubClientFactory(StubHandler.Json(body)));

        var act = () => resolver.ResolveDidAsync("owner.example");

        await act.Should().ThrowAsync<UpstreamUnavailableException>();
    }

    [Fact]
    public async Task AnOversizedAnswer_IsUnavailable_AndNeverReadWhole()
    {
        var resolver = new BlueskyHandleResolver(new StubClientFactory(StubHandler.Json(new string('x', 200_000))));

        var act = () => resolver.ResolveDidAsync("owner.example");

        await act.Should().ThrowAsync<UpstreamUnavailableException>();
    }

    [Fact]
    public async Task ACancelledCaller_IsNotReportedAsAnOutage()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var resolver = new BlueskyHandleResolver(new StubClientFactory(new ThrowingHandler(new OperationCanceledException(cancelled.Token))));

        var act = () => resolver.ResolveDidAsync("owner.example", cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>("a request the client abandoned is not a Bluesky outage");
    }
}
