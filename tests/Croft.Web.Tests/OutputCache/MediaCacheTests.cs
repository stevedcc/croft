using System.Net;
using AwesomeAssertions;

namespace Croft.Web.Tests.OutputCache;

/// <summary>
/// Brief 14c item 6: an image of a published post comes from the Web pod's memory after the first
/// request. The API's <c>Cache-Control</c> decides what is shared: only an anonymous request that the
/// API answered with a 200 and <c>public</c> is kept; a draft's image (<c>no-store</c>), a staff
/// member's request, any other status and an image above the size limit are never kept. The calls the
/// API sees are counted by the host's media responder (<see cref="CacheHost.MediaRequests"/>).
/// </summary>
public class MediaCacheTests
{
    private static readonly Guid Image = Guid.Parse("7b3f1c2e-0a54-4d6b-9e1d-2f4c8a6b5d10");

    private static async Task<(HttpStatusCode Status, byte[] Body, string Headers)> FetchAsync(
        HttpClient client, string path, bool staff = false, string? host = null)
    {
        using var request = CacheHost.Get(path, staff, host);
        using var response = await client.SendAsync(request);
        var headers = string.Join('\n', response.Headers.Concat(response.Content.Headers)
            .Where(h => h.Key is not ("Age" or "Content-Length" or "Transfer-Encoding" or "Date"))
            .OrderBy(h => h.Key, StringComparer.Ordinal)
            .Select(h => $"{h.Key}: {string.Join(", ", h.Value)}"));
        return (response.StatusCode, await response.Content.ReadAsByteArrayAsync(), headers);
    }

    [Fact]
    public async Task TwoAnonymousRequestsForAPublishedImage_AreOneApiCall_WithIdenticalBytesAndHeaders()
    {
        using var host = CacheHost.Create(media: _ => CacheHost.PublishedImage());
        using var client = host.Client();

        var first = await FetchAsync(client, $"/api/media/{Image}");
        var second = await FetchAsync(client, $"/api/media/{Image}");

        first.Status.Should().Be(HttpStatusCode.OK);
        second.Status.Should().Be(HttpStatusCode.OK);
        host.MediaRequests.Should().HaveCount(1, "the second request is the pod's copy");
        second.Body.Should().Equal(first.Body);
        second.Headers.Should().Be(first.Headers, "Content-Type, Cache-Control and the security headers are replayed with the image");
        first.Headers.Should().Contain("Content-Type: image/png")
            .And.Contain("Cache-Control: public, max-age=86400")
            .And.Contain("X-Content-Type-Options: nosniff");
    }

    [Fact]
    public async Task TwoAnonymousRequestsForADraftsImage_NoStore_AreTwoApiCalls()
    {
        using var host = CacheHost.Create(media: _ => CacheHost.DraftImage());
        using var client = host.Client();

        await FetchAsync(client, $"/api/media/{Image}");
        await FetchAsync(client, $"/api/media/{Image}");

        host.MediaRequests.Should().HaveCount(2, "the API said no-store: a draft's image is never kept");
        host.Store.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task TwoSignedInRequests_AreTwoApiCalls_AndCarryTheirOwnToken()
    {
        using var host = CacheHost.Create(media: _ => CacheHost.PublishedImage());
        using var client = host.Client();

        await FetchAsync(client, $"/api/media/{Image}", staff: true);
        await FetchAsync(client, $"/api/media/{Image}", staff: true);

        host.MediaRequests.Should().HaveCount(2, "staff see drafts: their requests are never answered from the shared copy");
        host.MediaRequests.Should().OnlyContain(r => r.Authorization == "Bearer test-token");
        host.Store.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task AnAnonymousRequest_AfterASignedInOne_StillFillsTheCache_AndAStaffRequestNeverReadsIt()
    {
        using var host = CacheHost.Create(media: _ => CacheHost.PublishedImage());
        using var client = host.Client();

        await FetchAsync(client, $"/api/media/{Image}", staff: true);
        await FetchAsync(client, $"/api/media/{Image}");
        await FetchAsync(client, $"/api/media/{Image}");
        await FetchAsync(client, $"/api/media/{Image}", staff: true);

        host.MediaRequests.Select(r => r.Authorization is not null).Should().Equal([true, false, true],
            "staff, then the anonymous fill, then (the second anonymous hit makes no call) staff again");
    }

    [Fact]
    public async Task AnImageOverTheLimit_IsServedWholeBothTimes_AsTwoApiCalls()
    {
        using var host = CacheHost.Create(media: _ => CacheHost.PublishedImage(bytes: 600 * 1024));
        using var client = host.Client();

        var first = await FetchAsync(client, $"/api/media/{Image}");
        var second = await FetchAsync(client, $"/api/media/{Image}");

        first.Body.Should().HaveCount(600 * 1024, "streamed whole, not truncated");
        second.Body.Should().HaveCount(600 * 1024);
        host.MediaRequests.Should().HaveCount(2);
        host.Store.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task AnImageAtTheLimit_IsKept_AndTheLimitIsTheSetting()
    {
        using var kept = CacheHost.Create(media: _ => CacheHost.PublishedImage(bytes: 500 * 1024));
        using var keptClient = kept.Client();
        using var small = CacheHost.Create(
            new() { ["Croft:Web:OutputCache:MediaMaximumBodyKilobytes"] = "1" }, media: _ => CacheHost.PublishedImage(bytes: 2048));
        using var smallClient = small.Client();

        await FetchAsync(keptClient, $"/api/media/{Image}");
        await FetchAsync(keptClient, $"/api/media/{Image}");
        await FetchAsync(smallClient, $"/api/media/{Image}");
        await FetchAsync(smallClient, $"/api/media/{Image}");

        kept.MediaRequests.Should().HaveCount(1, "500 KB is below the default 512 KB");
        small.MediaRequests.Should().HaveCount(2, "2 KB is above a limit of 1 KB");
    }

    [Fact]
    public async Task AStaffMediaDelete_DropsTheCachedImage_SoTheNextRequestCallsTheApi()
    {
        using var host = CacheHost.Create(media: _ => CacheHost.PublishedImage());
        using var client = host.Client();
        await FetchAsync(client, $"/api/media/{Image}");
        await FetchAsync(client, $"/api/media/{Image}");
        host.MediaRequests.Should().HaveCount(1);

        // The staff member's delete goes through the real API client, which evicts the content tag.
        await StaffApiClient.Create(new NoContentApi(), host.Store).DeleteMediaAsync(Image);
        await FetchAsync(client, $"/api/media/{Image}");

        host.Store.Evictions.Should().Equal("content");
        host.MediaRequests.Should().HaveCount(2, "the entry is tagged content, which every staff write evicts");
    }

    [Fact]
    public async Task TheQueryString_IsNotPartOfTheKey()
    {
        using var host = CacheHost.Create(media: _ => CacheHost.PublishedImage());
        using var client = host.Client();

        await FetchAsync(client, $"/api/media/{Image}?v=1");
        await FetchAsync(client, $"/api/media/{Image}?v=2");
        await FetchAsync(client, $"/api/media/{Image}");

        host.MediaRequests.Should().HaveCount(1);
    }

    [Fact]
    public async Task EachImageIsItsOwnEntry_AndTheHostHeaderDoesNotMintMore()
    {
        using var host = CacheHost.Create(media: _ => CacheHost.PublishedImage());
        using var client = host.Client();
        var other = Guid.Parse("11111111-2222-3333-4444-555555555555");

        await FetchAsync(client, $"/api/media/{Image}");
        await FetchAsync(client, $"/api/media/{other}");
        await FetchAsync(client, $"/api/media/{Image}", host: "mirror.example.net");
        await FetchAsync(client, $"/api/media/{other}", host: "elsewhere.example.org");

        host.MediaRequests.Select(r => r.Path).Should().Equal($"/api/media/{Image}", $"/api/media/{other}");
        host.Store.EntryCount.Should().Be(2);
    }

    [Fact]
    public async Task AnEntry_IsTaggedContent_AndLivesForTheDuration()
    {
        using var host = CacheHost.Create(media: _ => CacheHost.PublishedImage());
        using var client = host.Client();

        await FetchAsync(client, $"/api/media/{Image}");
        host.Clock.UtcNow += TimeSpan.FromSeconds(59);
        await FetchAsync(client, $"/api/media/{Image}");
        host.Clock.UtcNow += TimeSpan.FromSeconds(2);
        await FetchAsync(client, $"/api/media/{Image}");

        host.Store.Writes.Should().HaveCount(2).And.OnlyContain(
            w => w.Tags.SequenceEqual(new[] { "content" }) && w.ValidFor == TimeSpan.FromMinutes(1));
        host.MediaRequests.Should().HaveCount(2, "past the Duration the entry is gone and an unpublished post's image disappears");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task AnyStatusButOk_IsNeverKept(HttpStatusCode status)
    {
        using var host = CacheHost.Create(media: _ => new HttpResponseMessage(status));
        using var client = host.Client();

        var first = await FetchAsync(client, $"/api/media/{Image}");
        var afterFirst = host.MediaRequests.Count;
        await FetchAsync(client, $"/api/media/{Image}");

        first.Status.Should().Be(status);
        afterFirst.Should().BePositive();
        host.MediaRequests.Count.Should().BeGreaterThan(afterFirst, "the second request asks the API again (the client may retry a 5xx, so the count is not exact)");
        host.Store.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task AnApiThatIsDown_AnswersA503_ThatIsNeverKept()
    {
        using var host = CacheHost.Create(media: _ => throw new HttpRequestException("the API is down"));
        using var client = host.Client();

        var first = await FetchAsync(client, $"/api/media/{Image}");
        var afterFirst = host.MediaRequests.Count;
        await FetchAsync(client, $"/api/media/{Image}");

        first.Status.Should().Be(HttpStatusCode.ServiceUnavailable);
        host.MediaRequests.Count.Should().BeGreaterThan(afterFirst, "the second request asks the API again (the client retries, so the count is not exact)");
        host.Store.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task APublicAnswerThatIsNotPublic_IsNeverKept()
    {
        // "private" and a response with no Cache-Control at all are not "public".
        using var host = CacheHost.Create(media: request =>
        {
            var response = CacheHost.PublishedImage();
            response.Headers.CacheControl = request.RequestUri!.AbsolutePath.EndsWith("1111", StringComparison.Ordinal)
                ? null
                : System.Net.Http.Headers.CacheControlHeaderValue.Parse("private, max-age=60");
            return response;
        });
        using var client = host.Client();

        await FetchAsync(client, $"/api/media/{Image}");
        await FetchAsync(client, $"/api/media/{Image}");
        await FetchAsync(client, "/api/media/00000000-0000-0000-0000-000000001111");
        await FetchAsync(client, "/api/media/00000000-0000-0000-0000-000000001111");

        host.MediaRequests.Should().HaveCount(4);
        host.Store.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task ADurationOfZero_KeepsNoImages()
    {
        using var host = CacheHost.Create(new() { ["Croft:Web:OutputCache:Duration"] = "00:00:00" }, media: _ => CacheHost.PublishedImage());
        using var client = host.Client();

        await FetchAsync(client, $"/api/media/{Image}");
        await FetchAsync(client, $"/api/media/{Image}");

        host.MediaRequests.Should().HaveCount(2, "images live as long as the pages: the same Duration, and 0 turns it off");
    }

    /// <summary>The API answering a delete with 204.</summary>
    private sealed class NoContentApi : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
    }
}
