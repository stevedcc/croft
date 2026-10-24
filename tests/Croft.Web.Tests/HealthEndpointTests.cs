using System.Net;
using System.Xml.Linq;
using Croft.Web.Tests.Fixtures;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests;

/// <summary>
/// GET /health/live and GET /health/ready on the Web host: the contract the k8s probes (and any
/// other monitor) read. Liveness answers for the process alone — an API or database outage must
/// never restart Web pods, because a restart cannot fix a dependency — and readiness answers for
/// the state the pod must serve from: the Web database when one is configured, and the API's
/// /health. Both are anonymous (kubelet is not a signed-in user), plain, never cached and never
/// indexed.
/// </summary>
[Trait("Category", "Container")]
public class HealthEndpointTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private const string ConfiguredBaseUrl = "https://example.com/";

    // Nothing listens on 127.0.0.1:1: the connect is refused at once, so "the database is
    // unreachable" is deterministic and fast.
    private const string UnreachableWebDb = "Host=127.0.0.1;Port=1;Database=unreachable;Username=none;Password=none";

    [Fact]
    public async Task Ready_Returns200_WhenTheWebDatabaseAndTheApiAreHealthy()
    {
        var api = new StubApiHealthHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var factory = CreateFactory(fixture.ConnectionString, api);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "a pod that reaches its database and the API's /health is ready for traffic");
        api.RequestedPaths.Should().Equal(["/health"],
            "readiness probes the API's /health and nothing else");
    }

    [Fact]
    public async Task Ready_Returns200_WhenSecurityTxtIsBroken()
    {
        // Brief 11a item 2: a security.txt with no Contact and an unparseable Expires only logs
        // Warnings; it never fails startup or readiness.
        var folder = Path.Combine(Path.GetTempPath(), $"blog-health-sec-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(folder, ".well-known"));
        File.WriteAllText(Path.Combine(folder, ".well-known", "security.txt"), "Expires: not-a-date\n");
        try
        {
            using var factory = CreateFactory(fixture.ConnectionString,
                new StubApiHealthHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)), folder);
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/health/ready");

            response.StatusCode.Should().Be(HttpStatusCode.OK,
                "a broken security.txt must not take the pod out of rotation");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Ready_Returns503_WhenTheWebDatabaseIsUnreachable()
    {
        using var factory = CreateFactory(UnreachableWebDb,
            new StubApiHealthHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "a pod that cannot reach its Web database cannot serve signed-in users and must take no traffic");
    }

    [Fact]
    public async Task Ready_Returns503_WhenTheApiHealthFails()
    {
        using var factory = CreateFactory(fixture.ConnectionString,
            new StubApiHealthHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "a pod whose API answers /health unwell cannot serve content and must take no traffic");
    }

    [Fact]
    public async Task Ready_Returns503_WhenTheApiIsUnreachable()
    {
        using var factory = CreateFactory(fixture.ConnectionString,
            new StubApiHealthHandler(_ => throw new HttpRequestException("the API is down")));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "an API that cannot be reached at all fails readiness exactly like one that answers unwell");
    }

    [Fact]
    public async Task Live_Returns200_WhenTheWebDatabaseIsUnreachable()
    {
        using var factory = CreateFactory(UnreachableWebDb,
            new StubApiHealthHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "liveness is the process serving requests; a dependency outage must never restart Web pods");
    }

    [Fact]
    public async Task Live_Returns200_WhenTheApiHealthFails()
    {
        using var factory = CreateFactory(fixture.ConnectionString,
            new StubApiHealthHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "an API outage must never restart Web pods; only readiness may hold traffic back");
    }

    [Fact]
    public async Task Live_NamesTheReleasedVersion_InTheXCroftVersionHeader()
    {
        using var factory = CreateFactory(UnreachableWebDb,
            new StubApiHealthHandler(_ => throw new HttpRequestException("the API is down")));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("X-Croft-Version").Should().Equal([VersionFromProps()],
            "the release smoke test reads the version an image was built as from this header, with no API or database");
    }

    [Fact]
    public async Task BothEndpoints_AreAnonymous_Plain_NeverCached_NeverIndexed()
    {
        using var factory = CreateFactory(fixture.ConnectionString,
            new StubApiHealthHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        using var client = factory.CreateClient();

        foreach (var path in new[] { "/health/live", "/health/ready" })
        {
            using var response = await client.GetAsync(path);

            // This client is anonymous: 200 — not a challenge or a redirect to sign-in — is
            // the anonymity check. The probe endpoints carry no content and no user state.
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"{path} must answer an anonymous probe");
            response.Content.Headers.ContentType!.ToString().Should().Be("text/plain; charset=utf-8",
                $"{path} answers a small plain body, nothing more");
            response.Headers.CacheControl!.ToString().Should().Be("no-store",
                $"{path} is a point-in-time signal and must never be served from a cache");
            response.Headers.GetValues("X-Robots-Tag").Should().Contain("noindex",
                $"{path} is an operational surface and must never be indexed");
        }
    }

    [Fact]
    public async Task SitemapXml_ListsNoHealthEndpoint()
    {
        var api = new StubBlogApiHandler
        {
            Pages = [new(Guid.CreateVersion7(), "about", "About",
                Croft.Shared.Enums.PostStatus.Published, new(2026, 10, 5, 13, 45, 0, DateTimeKind.Utc))],
        };
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("Blog:BaseUrl", ConfiguredBaseUrl);
            builder.ConfigureServices(services =>
                services.AddHttpClient("BlogApi").ConfigurePrimaryHttpMessageHandler(() => api));
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/sitemap.xml");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var doc = XDocument.Parse(await response.Content.ReadAsStringAsync());
        var locs = doc.Root!.Elements(XName.Get("url", "http://www.sitemaps.org/schemas/sitemap/0.9"))
            .Elements(XName.Get("loc", "http://www.sitemaps.org/schemas/sitemap/0.9"))
            .Select(e => e.Value)
            .ToList();
        locs.Should().NotContain(loc => loc.Contains("/health", StringComparison.Ordinal),
            "the health endpoints are operational surfaces; a crawler must never be sent to them");
    }

    /// <summary>The API's /health over a stub: whatever the test needs it to answer (200, 503,
    /// or a connection failure) so the readiness check's reaction to each is the code under
    /// test. Every path the stub saw is recorded.</summary>
    private sealed class StubApiHealthHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> RequestedPaths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedPaths.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(respond(request));
        }
    }

    private static WebApplicationFactory<Program> CreateFactory(string? webDb, StubApiHealthHandler api, string? siteFilesPath = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("Blog:BaseUrl", ConfiguredBaseUrl);
            if (siteFilesPath is not null)
            {
                builder.UseSetting("Blog:SiteFilesPath", siteFilesPath);
            }

            if (webDb is not null)
            {
                builder.UseSetting("ConnectionStrings:WebDb", webDb);
            }

            builder.ConfigureServices(services =>
                services.AddHttpClient("BlogApi").ConfigurePrimaryHttpMessageHandler(() => api));
        });

    private static string VersionFromProps()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("Directory.Build.props sits at the repository root");
        var props = XDocument.Load(Path.Combine(directory!.FullName, "Directory.Build.props"));
        return props.Descendants("Version").Single().Value;
    }
}
