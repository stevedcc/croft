using System.Net;
using AwesomeAssertions;

namespace Croft.Web.Tests.OutputCache;

/// <summary>
/// Issue #5: every route that answers GET answers HEAD with the same status and headers and no body (link checkers and uptime
/// monitors use HEAD), and a HEAD neither stores an empty page in the output cache nor skips what a GET goes through.
/// </summary>
public class HeadRequestTests
{
    public static TheoryData<string> Routes =>
    [
        "/",
        $"/post/{CacheHost.PostSlug}",
        "/robots.txt",
        "/feed.xml",
        "/sitemap.xml",
        "/health/live",
    ];

    private static async Task<(HttpResponseMessage Response, byte[] Body)> SendAsync(HttpClient client, HttpMethod method, string path)
    {
        using var request = new HttpRequestMessage(method, path);
        var response = await client.SendAsync(request);
        return (response, await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Head_AnswersLikeGet_WithTheSameStatusAndHeaders_AndNoBody(string path)
    {
        using var host = CacheHost.Create();
        using var client = host.Client();

        var (get, getBody) = await SendAsync(client, HttpMethod.Get, path);
        var (head, headBody) = await SendAsync(client, HttpMethod.Head, path);

        get.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {path} works");
        getBody.Should().NotBeEmpty();
        head.StatusCode.Should().Be(HttpStatusCode.OK);
        head.Content.Headers.ContentType.Should().Be(get.Content.Headers.ContentType);
        var getCsp = get.Headers.TryGetValues("Content-Security-Policy", out var g) ? g.ToArray() : [];
        var headCsp = head.Headers.TryGetValues("Content-Security-Policy", out var h) ? h.ToArray() : [];
        headCsp.Should().Equal(getCsp, "a HEAD carries the same policy");
        if (get.Content.Headers.ContentType?.MediaType == "text/html")
        {
            getCsp.Should().NotBeEmpty("the comparison covers a real policy on the pages");
        }

        headBody.Should().BeEmpty("a HEAD answer has no body");
    }

    [Fact]
    public async Task Head_OnTheFeed_CarriesTheSameCacheControlAsTheGet()
    {
        using var host = CacheHost.Create();
        using var client = host.Client();

        var (get, _) = await SendAsync(client, HttpMethod.Get, "/feed.xml");
        var (head, _) = await SendAsync(client, HttpMethod.Head, "/feed.xml");

        get.Headers.CacheControl.Should().NotBeNull("the feed sends a Cache-Control, so the comparison is not null against null");
        head.Headers.CacheControl.Should().Be(get.Headers.CacheControl);
    }

    [Fact]
    public async Task AHeadOnAPageThatIsNotCachedYet_DoesNotStoreAnEmptyPage_ForTheGetThatFollows()
    {
        using var host = CacheHost.Create();
        using var client = host.Client();

        var (head, _) = await SendAsync(client, HttpMethod.Head, "/");
        var callsForTheHead = host.Calls;
        var (get, body) = await SendAsync(client, HttpMethod.Get, "/");

        head.StatusCode.Should().Be(HttpStatusCode.OK);
        host.Calls.Should().Be(callsForTheHead, "the HEAD rendered the page into the cache, so the GET is served from it");
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        System.Text.Encoding.UTF8.GetString(body).Should().Contain("Cache Post", "the GET after a HEAD is the full page, not an empty one from the cache");
    }

    [Fact]
    public async Task AHeadOnAMissingPath_IsA404LikeTheGet()
    {
        using var host = CacheHost.Create();
        using var client = host.Client();

        var (get, _) = await SendAsync(client, HttpMethod.Get, "/post/no-such-post");
        var (head, headBody) = await SendAsync(client, HttpMethod.Head, "/post/no-such-post");

        head.StatusCode.Should().Be(get.StatusCode);
        headBody.Should().BeEmpty();
    }

    [Fact]
    public async Task AHeadOnAStaffPage_IsTreatedAsTheGetIs_ARedirectToSignInForAnonymous()
    {
        using var host = CacheHost.Create(staffHeader: false);
        using var client = host.Client();

        var (get, _) = await SendAsync(client, HttpMethod.Get, "/admin/posts");
        var (head, headBody) = await SendAsync(client, HttpMethod.Head, "/admin/posts");

        head.StatusCode.Should().Be(get.StatusCode);
        head.Headers.Location.Should().Be(get.Headers.Location);
        headBody.Should().BeEmpty();
    }
}
