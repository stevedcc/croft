using System.Net;
using System.Net.Http.Headers;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Croft.Web.Tests;

/// <summary>
/// Brief 11a item 3: <c>GET /.well-known/webfinger?resource=&lt;r&gt;</c>. The resource
/// comparison is case-insensitive on the <c>acct:</c> scheme and host, exact on the local
/// part. 200 carries the file's exact bytes with <c>Content-Type: application/jrd+json</c> and
/// <c>Access-Control-Allow-Origin: *</c>; 404 for any other resource; 400 when
/// <c>resource</c> is missing. Same caching, ETag and no-cookie rules as the other site
/// files. A malformed webfinger file fails startup naming the file; a missing one is fine.
/// </summary>
public class WebFingerEndpointTests : IDisposable
{
    /// <summary>The JRD the tests publish; the served body must be these exact bytes.</summary>
    private const string WebfingerJson =
        """
        {
          "subject": "acct:steve@example.net",
          "aliases": ["acct:steve@users.example.net"]
        }
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"blog-webfinger-{Guid.NewGuid():N}");

    public WebFingerEndpointTests()
    {
        Directory.CreateDirectory(Path.Combine(_folder, ".well-known"));
        File.WriteAllText(Path.Combine(_folder, ".well-known", "webfinger"), WebfingerJson);
    }

    public void Dispose()
    {
        Directory.Delete(_folder, recursive: true);
        GC.SuppressFinalize(this);
    }

    private WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("Blog:BaseUrl", "https://example.com/");
            builder.UseSetting("Blog:SiteFilesPath", _folder);
        });

    [Fact]
    public async Task Post_Answers405WithAllowHeader()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/.well-known/webfinger?resource=acct:steve@example.net",
            new StringContent("{}"));

        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        response.Content.Headers.Allow.Should().BeEquivalentTo(["GET", "HEAD"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?resource=acct:someone-else@example.net")]
    public async Task ErrorAnswers_CarryNoCacheControlAndNoETag(string query)
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/.well-known/webfinger" + query);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.NotFound);
        response.Headers.CacheControl.Should().BeNull("an error answer must not be cached for an hour");
        response.Headers.ETag.Should().BeNull("the file's ETag belongs to the 200 answer only");
    }

    [Fact]
    public async Task ResourceMissing_Returns400()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/.well-known/webfinger");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Subject_Returns200WithExactBytesAndBothHeaders()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/.well-known/webfinger?resource=acct:steve@example.net");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be(WebfingerJson,
            "the endpoint serves the site's file untouched");
        response.Content.Headers.ContentType!.ToString().Should().Be("application/jrd+json");
        response.Headers.GetValues("Access-Control-Allow-Origin").Should().ContainSingle().And.Equal("*");
        response.Headers.CacheControl!.ToString().Should().Be("public, max-age=3600");
        response.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle().And.Equal("nosniff");
        response.Headers.ETag.Should().NotBeNull("same ETag rule as the other site files");
        response.Headers.Contains("Set-Cookie").Should().BeFalse("site files set no cookies");
        response.Headers.Contains("Content-Security-Policy").Should()
            .BeFalse("site files are not documents and never carry a CSP document header");
    }

    [Fact]
    public async Task IfNoneMatch_WithMatchingETag_Returns304WithNoBody()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        // First request: get the ETag the server hands out.
        using var first = await client.GetAsync("/.well-known/webfinger?resource=acct:steve@example.net");
        var etag = first.Headers.ETag!.Tag;

        // Second request: with If-None-Match, the server answers 304 and an empty body.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/.well-known/webfinger?resource=acct:steve@example.net");
        request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag));
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotModified,
            "a matching ETag lets the cache answer 304 with no body");
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Alias_Returns200()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/.well-known/webfinger?resource=acct:steve@users.example.net");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be(WebfingerJson,
            "an alias resolves to the same document's exact bytes (review finding #5)");
    }

    [Fact]
    public async Task AnotherAccountOnSameHost_Returns404()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        // Same host as the subject, different account: the WebFinger document is one
        // person's, so another account on this host must not resolve to it.
        using var response = await client.GetAsync("/.well-known/webfinger?resource=acct:other@example.net");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Subject_WithUppercaseAcctAndHost_Returns200()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        // The brief's matching rule: the acct: scheme and the host are case-insensitive.
        using var response = await client.GetAsync("/.well-known/webfinger?resource=ACCT:steve@EXAMPLE.NET");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Subject_WithDifferentLocal_Returns404()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        // The local part is exact-matched (Ordinal): changing case in the local part is a
        // different handle.
        using var response = await client.GetAsync("/.well-known/webfinger?resource=acct:STEVE@example.net");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Subject_WithDifferentHost_Returns404()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/.well-known/webfinger?resource=acct:steve@example.com");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Subject_WithMalformedAcct_Returns404()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        // acct: with no '@' is unusual; the host comparison is the only thing it could match,
        // and without a host there is nothing to compare.
        using var response = await client.GetAsync("/.well-known/webfinger?resource=acct:nosuchhost");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public void CaseVariantWebfingerFileName_FailsStartup_NamingTheFile()
    {
        // Review finding #2 asked what a file named ".well-known/WEBFINGER" does. Answer, by
        // evidence: SiteFilesService's allowed-name rule is exact (item 1), so the case
        // variant fails startup naming the file and no WebFinger endpoint is ever registered
        // at a non-RFC path — nothing silently breaks. RFC 7033's path is case-sensitive, so
        // the loud rejection is the right outcome; the owner renames the file.
        var folder = Path.Combine(Path.GetTempPath(), $"blog-webfinger-case-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(folder, ".well-known"));
        File.WriteAllText(Path.Combine(folder, ".well-known", "WEBFINGER"), WebfingerJson);
        try
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
                builder.UseSetting("Blog:BaseUrl", "https://example.com/");
                builder.UseSetting("Blog:SiteFilesPath", folder);
            });

            var act = () => factory.CreateClient();

            act.Should().Throw<SiteFilesException>()
                .WithMessage("*WEBFINGER*not a known well-known file*");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void MalformedJsonAtStartupFailsStartup_NamingTheFile()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"blog-webfinger-bad-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(folder, ".well-known"));
        File.WriteAllText(Path.Combine(folder, ".well-known", "webfinger"), "not json at all");
        try
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
                builder.UseSetting("Blog:BaseUrl", "https://example.com/");
                builder.UseSetting("Blog:SiteFilesPath", folder);
            });

            // The parse runs at startup: booting the app (CreateClient) must throw, naming
            // the file — a malformed webfinger file is never silently served.
            var act = () => factory.CreateClient();

            act.Should().Throw<SiteFilesException>()
                .WithMessage("*webfinger*not valid JSON*");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void NoSubjectField_FailsStartup_NamingTheFile()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"blog-webfinger-nosubj-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(folder, ".well-known"));
        File.WriteAllText(Path.Combine(folder, ".well-known", "webfinger"),
            """{"aliases":["acct:steve@example.net"]}""");
        try
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
                builder.UseSetting("Blog:BaseUrl", "https://example.com/");
                builder.UseSetting("Blog:SiteFilesPath", folder);
            });

            var act = () => factory.CreateClient();

            act.Should().Throw<SiteFilesException>()
                .WithMessage("*webfinger*missing the required 'subject'*");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task NoWebfingerFile_AppStartsAndThePathIsNotServed()
    {
        // The webfinger file is optional (brief 11a item 1: "a missing folder is fine: no
        // site files"): a site that publishes no webfinger still boots and serves its other
        // site files, and the webfinger path answers nothing.
        File.WriteAllText(Path.Combine(_folder, "humans.txt"), "WE ARE HUMANS\n");
        File.Delete(Path.Combine(_folder, ".well-known", "webfinger"));
        using var factory = Factory();
        using var client = factory.CreateClient();

        using var humans = await client.GetAsync("/humans.txt");
        humans.StatusCode.Should().Be(HttpStatusCode.OK,
            "a site without a webfinger file boots and serves its other site files");
        (await humans.Content.ReadAsStringAsync()).Should().Be("WE ARE HUMANS\n");

        using var webfinger = await client.GetAsync("/.well-known/webfinger?resource=acct:steve@example.net");
        webfinger.StatusCode.Should().NotBe(HttpStatusCode.OK,
            "no webfinger file means no WebFinger endpoint, not 200 with something else");
    }
}
