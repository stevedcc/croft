using System.Net;
using AwesomeAssertions;
using Croft.Shared.Contracts.Social;
using Croft.Web.Tests.OutputCache;

namespace Croft.Web.Tests;

/// <summary>Brief 12c item 1: the Web client's staff calls for the discussion (admin read, refresh, blocks).</summary>
public class DiscussionAdminApiClientTests
{
    private static readonly Guid PostId = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid BlockId = Guid.Parse("00000000-0000-0000-0000-0000000000c1");

    private static readonly SocialBlockDto Block = new(BlockId, SocialPlatform.Mastodon, SocialBlockKind.Account,
        "acct-1", "Reader (@reader@social.example)", "spam", "Admin", new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero));

    private sealed record Seen(HttpMethod Method, string PathAndQuery, string? Body, string? Authorization);

    private sealed class Api(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Seen(request.Method, request.RequestUri!.PathAndQuery, body, request.Headers.Authorization?.ToString()));
            return answer(request);
        }
    }

    private static (Services.BlogApiClient Client, Api Api, RecordingStore Store) Create(Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        var api = new Api(answer);
        var store = new RecordingStore(TimeProvider.System);
        return (StaffApiClient.Create(api, store), api, store);
    }

    private static HttpResponseMessage Status(HttpStatusCode status) => new(status);

    private static HttpResponseMessage Json<T>(HttpStatusCode status, T value) => new(status) { Content = JsonContent.Create(value) };

    [Fact]
    public async Task GetDiscussionAdminAsync_GetsTheAdminRoute_WithTheToken_AndReadsTheDto()
    {
        var expected = new PostDiscussionAdminDto(
            new PostDiscussionDto("https://social.example/@owner/1", null, 0, []),
            [new DiscussionThreadStatusDto(SocialPlatform.Mastodon, null, null, "boom", null, 0, false)]);
        var (client, api, store) = Create(_ => Json(HttpStatusCode.OK, expected));

        var result = await client.GetDiscussionAdminAsync(PostId);

        result.Should().BeEquivalentTo(expected);
        api.Requests.Should().ContainSingle().Which.Should().Be(
            new Seen(HttpMethod.Get, $"/api/posts/{PostId}/discussion/admin", null, "Bearer test-token"));
        store.Evictions.Should().BeEmpty("a read changes nothing");
    }

    [Fact]
    public async Task GetDiscussionAdminAsync_IsNullFor404()
    {
        var (client, _, _) = Create(_ => Status(HttpStatusCode.NotFound));

        (await client.GetDiscussionAdminAsync(PostId)).Should().BeNull();
    }

    [Fact]
    public async Task GetDiscussionAdminAsync_ThrowsForAServerError()
    {
        var (client, _, _) = Create(_ => Status(HttpStatusCode.InternalServerError));

        var call = () => client.GetDiscussionAdminAsync(PostId);

        await call.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task RefreshDiscussionAsync_PostsTheRefreshRoute_WithNoBody_AndEvictsNothing()
    {
        var (client, api, store) = Create(_ => Status(HttpStatusCode.Accepted));

        await client.RefreshDiscussionAsync(PostId);

        api.Requests.Should().ContainSingle().Which.Should().Be(
            new Seen(HttpMethod.Post, $"/api/posts/{PostId}/discussion/refresh", null, "Bearer test-token"));
        store.Evictions.Should().BeEmpty("a refresh changes no page the readers see");
    }

    [Fact]
    public async Task RefreshDiscussionAsync_ThrowsWhenTheApiRefuses()
    {
        var (client, _, _) = Create(_ => Status(HttpStatusCode.NotFound));

        var call = () => client.RefreshDiscussionAsync(PostId);

        await call.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task GetSocialBlocksAsync_GetsTheBlocksRoute_AndKeepsTheOrder()
    {
        var older = Block with { Id = Guid.NewGuid(), Label = "older" };
        var (client, api, store) = Create(_ => Json(HttpStatusCode.OK, new[] { Block, older }));

        var result = await client.GetSocialBlocksAsync();

        result.Should().Equal([Block, older]);
        api.Requests.Should().ContainSingle().Which.Should().Be(
            new Seen(HttpMethod.Get, "/api/discussion/blocks", null, "Bearer test-token"));
        store.Evictions.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.OK)]
    public async Task CreateSocialBlockAsync_PostsTheBody_AndReturnsTheBlock_For201And200(HttpStatusCode status)
    {
        var request = new CreateSocialBlockRequest(SocialPlatform.Mastodon, SocialBlockKind.Account, "acct-1", "Reader", "spam");
        var (client, api, _) = Create(_ => Json(status, Block));

        var result = await client.CreateSocialBlockAsync(request);

        result.Should().Be(Block);
        var seen = api.Requests.Should().ContainSingle().Subject;
        seen.Method.Should().Be(HttpMethod.Post);
        seen.PathAndQuery.Should().Be("/api/discussion/blocks");
        seen.Authorization.Should().Be("Bearer test-token");
        System.Text.Json.JsonSerializer.Deserialize<CreateSocialBlockRequest>(seen.Body!, System.Text.Json.JsonSerializerOptions.Web)
            .Should().Be(request);
    }

    [Fact]
    public async Task CreateSocialBlockAsync_ThrowsWithTheApisDetail_WhenItAnswers400()
    {
        var (client, _, store) = Create(_ => Json(HttpStatusCode.BadRequest,
            new Microsoft.AspNetCore.Mvc.ProblemDetails { Detail = "ExternalId is required." }));

        var call = () => client.CreateSocialBlockAsync(new CreateSocialBlockRequest(SocialPlatform.Bluesky, SocialBlockKind.Reply, "", null, null));

        (await call.Should().ThrowAsync<HttpRequestException>()).WithMessage("ExternalId is required.");
        store.Evictions.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteSocialBlockAsync_DeletesTheRoute_AndIsTrueFor204()
    {
        var (client, api, _) = Create(_ => Status(HttpStatusCode.NoContent));

        (await client.DeleteSocialBlockAsync(BlockId)).Should().BeTrue();

        api.Requests.Should().ContainSingle().Which.Should().Be(
            new Seen(HttpMethod.Delete, $"/api/discussion/blocks/{BlockId}", null, "Bearer test-token"));
    }

    [Fact]
    public async Task DeleteSocialBlockAsync_IsFalseFor404_AndEvictsNothing()
    {
        var (client, _, store) = Create(_ => Status(HttpStatusCode.NotFound));

        (await client.DeleteSocialBlockAsync(BlockId)).Should().BeFalse();
        store.Evictions.Should().BeEmpty("nothing changed");
    }

    [Fact]
    public async Task DeleteSocialBlockAsync_ThrowsForAServerError()
    {
        var (client, _, store) = Create(_ => Status(HttpStatusCode.InternalServerError));

        var call = () => client.DeleteSocialBlockAsync(BlockId);

        await call.Should().ThrowAsync<HttpRequestException>();
        store.Evictions.Should().BeEmpty();
    }

    [Fact]
    public async Task CreatingABlock_EvictsTheReadingPages_AfterTheApiAnswers()
    {
        var (client, _, store) = Create(_ => Json(HttpStatusCode.Created, Block));

        await client.CreateSocialBlockAsync(new CreateSocialBlockRequest(SocialPlatform.Mastodon, SocialBlockKind.Account, "acct-1", null, null));

        store.Evictions.Should().Equal("content");
    }

    [Fact]
    public async Task DeletingABlock_EvictsTheReadingPages_AfterTheApiAnswers()
    {
        var (client, _, store) = Create(_ => Status(HttpStatusCode.NoContent));

        await client.DeleteSocialBlockAsync(BlockId);

        store.Evictions.Should().Equal("content");
    }
}
