using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Croft.Web.Tests;

/// <summary>
/// The plain security headers on every Web response, with their exact values, on a page, a
/// static file, a 404 and a redirect. HSTS is sent outside Development on any scheme (TLS
/// terminates at the ingress, so the app must not judge by the request), exactly once;
/// Kestrel's Server header is off (the option is asserted because TestServer never sends one).
/// Content-Security-Policy is deliberately not pinned here: Blazor's built-in
/// frame-ancestors header is the framing guard until the app's own policy replaces it in a
/// later change, and that change asserts the header's name, count and value itself.
/// </summary>
public sealed class SecurityHeadersTests : IDisposable
{
    // The sign-out redirect exists only when OIDC is configured; nothing ever talks to this
    // IdP. HTTPS because the scheme's post-configure refuses a plain-HTTP authority outside
    // Development, and these tests run the production settings.
    private const string Authority = "https://idp.example.com";
    private const string ClientId = "blog-web";

    private const string MediaPath = "/api/media/0199a1b2-0000-7000-8000-000000000001";

    private readonly string _siteFiles = Path.Combine(Path.GetTempPath(), $"blog-security-headers-{Guid.NewGuid():N}");

    public SecurityHeadersTests()
    {
        Directory.CreateDirectory(Path.Combine(_siteFiles, ".well-known"));
        File.WriteAllText(Path.Combine(_siteFiles, ".well-known", "webfinger"),
            """{ "subject": "acct:steve@example.net" }""");
        File.WriteAllText(Path.Combine(_siteFiles, ".well-known", "security.txt"),
            "Contact: mailto:security@example.net\nExpires: 2099-01-01T00:00:00Z\n");
    }

    public void Dispose() => Directory.Delete(_siteFiles, recursive: true);

    /// <summary>
    /// Every route class with the Cross-Origin-Resource-Policy it gets: same-origin for what only
    /// this site's own documents load (the documents, the static assets, the avatars, theme.css,
    /// the probes); cross-origin for what other sites and browser-based readers fetch (the media
    /// images, the feed, the sitemap, robots.txt, the well-known files, the manifest and the
    /// branding images).
    /// </summary>
    public static TheoryData<string, bool, string, string> CorpRoutes => new()
    {
        { "/", false, "same-origin", "a static reading page" },
        { "/admin/posts", true, "same-origin", "an interactive staff page" },
        { "/app.css", false, "same-origin", "a static stylesheet" },
        { "/js/static-glue.js", false, "same-origin", "a static script" },
        { "/fonts/CroftSans-Regular.woff2", false, "same-origin", "a font" },
        { $"/social/avatar/{new string('a', 64)}.webp", false, "same-origin", "a social avatar" },
        { "/branding/theme.css", false, "same-origin", "the theme stylesheet" },
        { "/health/live", false, "same-origin", "the liveness probe" },
        { MediaPath, false, "cross-origin", "a media image" },
        { "/feed.xml", false, "cross-origin", "the feed" },
        { "/sitemap.xml", false, "cross-origin", "the sitemap" },
        { "/robots.txt", false, "cross-origin", "robots.txt" },
        { "/.well-known/webfinger?resource=acct:steve@example.net", false, "cross-origin", "WebFinger" },
        { "/.well-known/security.txt", false, "cross-origin", "security.txt" },
        { "/manifest.webmanifest", false, "cross-origin", "the web app manifest" },
        { "/branding/logo.svg", false, "cross-origin", "a branding image" },
    };

    [Theory]
    [MemberData(nameof(CorpRoutes))]
    public async Task Corp_HasTheExactValueOfItsRouteClass_OnGetAndHead(string path, bool signedIn, string corp, string shape)
    {
        using var factory = CreateFactory(signedIn: signedIn, siteFiles: _siteFiles);
        using var client = CreateClient(factory);

        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Head })
        {
            using var request = new HttpRequestMessage(method, path);
            using var response = await client.SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.OK, $"{method} {shape} is served");
            response.Headers.Contains("Cross-Origin-Resource-Policy").Should().BeTrue($"{method} {shape} carries a Cross-Origin-Resource-Policy");
            response.Headers.GetValues("Cross-Origin-Resource-Policy").Should().ContainSingle(
                $"{method} {shape} carries one Cross-Origin-Resource-Policy").Which.Should().Be(corp, $"{shape} is {corp}");
        }
    }

    [Theory]
    [InlineData("/", HttpStatusCode.OK, "the home page")]
    [InlineData("/app.css", HttpStatusCode.OK, "a static file")]
    [InlineData("/no-such-page", HttpStatusCode.NotFound, "an unknown URL")]
    [InlineData("/logout", HttpStatusCode.Redirect, "the sign-out redirect")]
    public async Task PlainHeaders_HaveTheExactValues_OnEveryResponseShape(
        string path, HttpStatusCode status, string shape)
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);
        using var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(status, shape);
        AssertPlainSecurityHeaders(response);
    }

    [Theory]
    [InlineData("/", false, "a static reading page")]
    [InlineData("/admin/posts", true, "an interactive staff page")]
    public async Task Coep_IsRequireCorp_OnEveryDocument(string path, bool signedIn, string shape)
    {
        using var factory = CreateFactory(signedIn: signedIn);
        using var client = CreateClient(factory);
        using var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK, shape);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html", $"{shape} is a document");
        AssertSingleHeader(response, "Cross-Origin-Embedder-Policy", "require-corp");
    }

    [Theory]
    [InlineData("/robots.txt", "robots.txt")]
    [InlineData("/feed.xml", "the feed")]
    [InlineData(MediaPath, "a media image")]
    [InlineData("/app.css", "a static asset")]
    public async Task Coep_IsNotSent_OnResponsesThatAreNotDocuments(string path, string shape)
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);
        using var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK, shape);
        response.Content.Headers.ContentType!.MediaType.Should().NotBe("text/html", $"{shape} is not a document");
        response.Headers.Contains("Cross-Origin-Embedder-Policy").Should().BeFalse(
            $"COEP governs what a document embeds; {shape} embeds nothing");
    }

    [Fact]
    public async Task Hsts_IsNotSent_InDevelopment()
    {
        using var factory = CreateFactory(production: false);
        using var client = CreateClient(factory);
        using var response = await client.GetAsync("/robots.txt");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("Strict-Transport-Security").Should().BeFalse(
            "HSTS is for real deployments; the dev server answers plain HTTP and must not pin it");
    }

    [Fact]
    public void ServerHeader_IsTurnedOffOnKestrel()
    {
        using var factory = CreateFactory();

        // TestServer never sends a Server header, so the Kestrel option is where the production
        // behaviour can be proven; the response shapes above prove the header is absent.
        factory.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.AddServerHeader
            .Should().BeFalse("Kestrel's default Server: Kestrel names the server for no benefit");
    }

    private static WebApplicationFactory<Program> CreateFactory(bool production = true, bool signedIn = false, string? siteFiles = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("Blog:BaseUrl", "https://example.com/");
            // Outside Development the app refuses to start without the analytics ingest key.
            builder.UseSetting("Analytics:IngestKey", "test-ingest-key");
            builder.UseSetting("Oidc:Authority", Authority);
            builder.UseSetting("Oidc:ClientId", ClientId);
            if (siteFiles is not null)
            {
                builder.UseSetting("Blog:SiteFilesPath", siteFiles);
            }
            if (production)
            {
                builder.UseEnvironment("Production");
            }

            builder.ConfigureServices(services =>
            {
                services.AddHttpClient("BlogApi").ConfigurePrimaryHttpMessageHandler(() => new ImageServingApi());
                if (signedIn)
                {
                    Fixtures.SignedInAdmin.AddTo(services);
                }
            });
        });

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static void AssertPlainSecurityHeaders(HttpResponseMessage response)
    {
        AssertSingleHeader(response, "Strict-Transport-Security", "max-age=31536000; includeSubDomains");
        AssertSingleHeader(response, "X-Content-Type-Options", "nosniff");
        AssertSingleHeader(response, "Referrer-Policy", "strict-origin-when-cross-origin");
        AssertSingleHeader(response, "Permissions-Policy", "camera=(), microphone=(), geolocation=()");
        AssertSingleHeader(response, "Cross-Origin-Opener-Policy", "same-origin");
        AssertSingleHeader(response, "Cross-Origin-Resource-Policy", "same-origin");
        response.Headers.Contains("Server").Should().BeFalse("the server must not be named");
    }

    private static void AssertSingleHeader(HttpResponseMessage response, string name, string value)
    {
        response.Headers.Contains(name).Should().BeTrue($"every response carries {name}");
        response.Headers.GetValues(name).Should().ContainSingle(
            $"{name} must appear exactly once").Which.Should().Be(value);
    }

    /// <summary>The stub API, plus a small PNG for the media and avatar reads the Web app proxies.</summary>
    private sealed class ImageServingApi : DelegatingHandler
    {
        private static readonly byte[] Png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

        public ImageServingApi() : base(new StubBlogApiHandler())
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (!path.StartsWith("/api/media/", StringComparison.Ordinal) && !path.StartsWith("/api/social/avatars/", StringComparison.Ordinal))
            {
                return base.SendAsync(request, cancellationToken);
            }

            var content = new ByteArrayContent(Png);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
