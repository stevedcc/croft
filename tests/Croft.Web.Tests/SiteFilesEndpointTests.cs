using System.Net;
using System.Net.Http.Headers;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Croft.Web.Tests;

/// <summary>
/// Brief 11a item 1: the site-files endpoint serves each known file's exact bytes back, with a
/// strong content-hash ETag, a one-hour Cache-Control, and X-Content-Type-Options: nosniff. It
/// answers only GET and HEAD (every other method is 405 with `Allow: GET, HEAD`), sets no cookies, and never emits a
/// Content-Security-Policy document header — site files are not documents on the site's origin.
/// </summary>
public class SiteFilesEndpointTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"blog-site-files-ep-{Guid.NewGuid():N}");

    public SiteFilesEndpointTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        Directory.Delete(_folder, recursive: true);
        GC.SuppressFinalize(this);
    }

    private WebApplicationFactory<Program> Factory()
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("Blog:BaseUrl", "https://example.com/");
            builder.UseSetting("Blog:SiteFilesPath", _folder);
        });
    }

    private static void WriteFile(string folder, string relative, string content)
    {
        var path = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
        var parent = Path.GetDirectoryName(path)!;
        if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
        {
            Directory.CreateDirectory(parent);
        }

        File.WriteAllText(path, content);
    }

    [Fact]
    public async Task Get_ReturnsExactBytesAndContentTypeAndHeaders()
    {
        WriteFile(_folder, "humans.txt", "WE ARE HUMANS\n");
        using var factory = Factory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/humans.txt");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("WE ARE HUMANS\n");
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");
        response.Content.Headers.ContentType.CharSet.Should().Be("utf-8");
        response.Headers.CacheControl!.ToString().Should().Be("public, max-age=3600");
        response.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle().And.Equal("nosniff");
        response.Headers.ETag.Should().NotBeNull("every site file carries a strong content-hash ETag");
        response.Headers.Contains("Set-Cookie").Should().BeFalse("site files set no cookies");
        response.Headers.Contains("Content-Security-Policy").Should()
            .BeFalse("site files are not documents and never carry a CSP document header");
    }

    [Fact]
    public async Task Head_ReturnsHeadersButEmptyBody()
    {
        WriteFile(_folder, "humans.txt", "WE ARE HUMANS\n");
        using var factory = Factory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Head, "/humans.txt");
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty("HEAD carries no body");
        response.Headers.ETag.Should().NotBeNull("HEAD still carries the ETag, so a cache can update");
        response.Headers.CacheControl!.ToString().Should().Be("public, max-age=3600");
    }

    [Fact]
    public async Task IfNoneMatch_WithMatchingETag_Returns304WithNoBody()
    {
        WriteFile(_folder, "humans.txt", "WE ARE HUMANS\n");
        using var factory = Factory();
        using var client = factory.CreateClient();

        // First request: get the ETag the server hands out.
        using var first = await client.GetAsync("/humans.txt");
        var etag = first.Headers.ETag!.Tag;

        // Second request: with If-None-Match, the server should answer 304 and an empty body.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/humans.txt");
        request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag));
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotModified,
            "a matching ETag lets the cache answer 304 with no body");
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task UnsafeMethods_Answer405WithAllowHeader_AndDoNotChangeTheFile(string method)
    {
        // The brief: "POST is 405". Without an endpoint of its own for the other methods the
        // request fell through to the Blazor catch-all, where UseAntiforgery turned POST into 400.
        WriteFile(_folder, "humans.txt", "WE ARE HUMANS\n");
        using var factory = Factory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(new HttpMethod(method), "/humans.txt")
        {
            Content = new StringContent("""{"would":"overwrite"}""", System.Text.Encoding.UTF8, "application/json"),
        };
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        response.Content.Headers.Allow.Should().BeEquivalentTo(["GET", "HEAD"]);

        using var get = await client.GetAsync("/humans.txt");
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        (await get.Content.ReadAsStringAsync()).Should().Be("WE ARE HUMANS\n",
            "the rejected request must not modify the file the URL points at");
    }

    [Fact]
    public void SiteFileAtARealStaticAssetUrl_FailsStartupNamingBoth()
    {
        // wwwroot/fonts/OFL-SourceSerif4.txt is served by MapStaticAssets; a site file at the
        // same URL would be silently dropped (the asset wins), so startup must refuse it.
        WriteFile(_folder, "fonts/OFL-SourceSerif4.txt", "shadowing");
        using var factory = Factory();

        var act = () => factory.CreateClient();

        act.Should().Throw<Exception>()
            .Which.ToString().Should().Contain("/fonts/OFL-SourceSerif4.txt").And.Contain("static asset");
    }

    [Fact]
    public void SiteFileNamedLikeARouteTemplate_FailsStartup_AndNoWildcardRouteIsMapped()
    {
        WriteFile(_folder, "{id}.txt", "route injection probe");
        using var factory = Factory();

        var act = () => factory.CreateClient();

        act.Should().Throw<Exception>().Which.ToString().Should().Contain("{id}.txt");
    }

    [Fact]
    public async Task GetUnknownSiteFile_DoesNotReturn200()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        // No site file behind this path: the endpoint isn't mapped here, so the request reaches
        // Blazor's catch-all. The test only verifies the path is not answered with the empty
        // bytes — anything else is acceptable so long as it isn't 200 with the site-files
        // response shape (a strong ETag, text/plain content-type).
        using var response = await client.GetAsync("/.well-known/no-such-file");

        response.StatusCode.Should().NotBe(HttpStatusCode.OK,
            "no site file at this path means the request reaches the Blazor router, not 200 with empty bytes");
    }

    [Fact]
    public async Task GetAppReservedPath_StillReturnsItsOwnResponse_NotTheSiteFilesEndpoint()
    {
        // The site-files endpoint is per-file, so /robots.txt is the app's robots.txt, not the
        // site files'. Without a site file at /robots.txt the call still hits the app's
        // robots.txt endpoint.
        using var factory = Factory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/robots.txt");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "/robots.txt is the app's own endpoint; no site file shadows it");
        (await response.Content.ReadAsStringAsync()).Should().Contain("User-agent: *");
    }

    [Fact]
    public async Task JsonSiteFile_ReturnsExactBytes_AsApplicationJson()
    {
        WriteFile(_folder, ".well-known/apple-app-site-association", """{"applinks":{}}""");
        using var factory = Factory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/.well-known/apple-app-site-association");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("""{"applinks":{}}""");
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
    }
}