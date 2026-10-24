using System.Net;
using AwesomeAssertions;
using Croft.Shared.Contracts.Social;
using Croft.Web.Tests.OutputCache;

namespace Croft.Web.Tests;

/// <summary>Brief 12d item 6: the Web client's calls for a post's announcements (the status read, "Announce now" and "Retry").</summary>
public class AnnouncementsApiClientTests
{
    private static readonly Guid PostId = Guid.Parse("00000000-0000-0000-0000-0000000000e1");

    private sealed record Seen(HttpMethod Method, string PathAndQuery, string? Authorization);

    private sealed class Api(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new Seen(request.Method, request.RequestUri!.PathAndQuery, request.Headers.Authorization?.ToString()));
            return Task.FromResult(answer(request));
        }
    }

    private static (Services.BlogApiClient Client, Api Api, RecordingStore Store) Create(Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        var api = new Api(answer);
        var store = new RecordingStore(TimeProvider.System);
        return (StaffApiClient.Create(api, store), api, store);
    }

    private static HttpResponseMessage Json<T>(HttpStatusCode status, T value) => new(status) { Content = JsonContent.Create(value) };

    private static readonly PostAnnouncementsDto Announcements = new(true, false, "https://blog.example/post/a-post",
        [new AnnouncementStatusDto(SocialPlatform.Mastodon, AnnouncementState.Pending, 1, new DateTime(2026, 10, 23, 8, 0, 0, DateTimeKind.Utc),
            NextAttemptAt: new DateTime(2026, 10, 23, 8, 2, 0, DateTimeKind.Utc), LastError: "Mastodon answered HTTP 503")]);

    [Fact]
    public async Task GetAnnouncementsAsync_GetsTheRoute_WithTheToken_AndReadsTheDto()
    {
        var (client, api, store) = Create(_ => Json(HttpStatusCode.OK, Announcements));

        var result = await client.GetAnnouncementsAsync(PostId);

        result.Should().BeEquivalentTo(Announcements);
        api.Requests.Should().ContainSingle().Which.Should().Be(new Seen(HttpMethod.Get, $"/api/posts/{PostId}/announcements", "Bearer test-token"));
        store.Evictions.Should().BeEmpty("a read changes nothing");
    }

    [Fact]
    public async Task GetAnnouncementsAsync_IsNullFor404_AndThrowsForAServerError()
    {
        var (missing, _, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        (await missing.GetAnnouncementsAsync(PostId)).Should().BeNull();

        var (broken, _, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var call = () => broken.GetAnnouncementsAsync(PostId);
        await call.Should().ThrowAsync<HttpRequestException>();
    }

    [Theory]
    [InlineData(SocialPlatform.Mastodon, "mastodon")]
    [InlineData(SocialPlatform.Bluesky, "bluesky")]
    public async Task AnnounceAsync_PostsTheRoute_WithNoBody_AndReadsTheRow(SocialPlatform platform, string segment)
    {
        var row = Announcements.Items[0] with { Platform = platform };
        var (client, api, store) = Create(_ => Json(HttpStatusCode.Accepted, row));

        var result = await client.AnnounceAsync(PostId, platform);

        result.Should().BeEquivalentTo(row);
        api.Requests.Should().ContainSingle().Which.Should().Be(new Seen(HttpMethod.Post, $"/api/posts/{PostId}/announcements/{segment}", "Bearer test-token"));
        store.Evictions.Should().BeEmpty("the post is not changed until the sender has posted");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "Bluesky is not set up on this site.")]
    [InlineData(HttpStatusCode.Conflict, "The post is not published.")]
    public async Task AnnounceAsync_ThrowsWithTheApisDetail_For400And409(HttpStatusCode status, string detail)
    {
        var (client, _, _) = Create(_ => Json(status, new Microsoft.AspNetCore.Mvc.ProblemDetails { Detail = detail }));

        var call = () => client.AnnounceAsync(PostId, SocialPlatform.Bluesky);

        (await call.Should().ThrowAsync<HttpRequestException>()).WithMessage(detail);
    }
}
