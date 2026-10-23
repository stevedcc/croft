using System.Net;
using AwesomeAssertions;
using Croft.Web.Security.Csp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests;

/// <summary>
/// The CSP header is built once per variant, not once per response (brief 13e item 3, review
/// I-4): the contributors run when a variant is first seen, every later response of that variant
/// takes the finished strings, and no response builds a mutable policy unless its code asks the
/// ICspFeature for one. The exact header values are CspPolicySnapshotTests' and CspHeaderTests'
/// assertions (CspSnapshots.txt is unchanged by this item).
/// </summary>
public class CspHeaderCostTests
{
    [Fact]
    public async Task Contributors_RunOncePerVariant_NotOncePerResponse()
    {
        var counts = new Counts();
        using var factory = CreateFactory(services =>
        {
            services.AddSingleton<ICspContributor>(new CountingContributor(counts, key: ""));
            services.AddSingleton<IDocumentCspContributor>(new CountingDocumentContributor(counts, key: ""));
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var homeHeaders = new List<string>();
        for (var i = 0; i < 25; i++)
        {
            using var home = await client.GetAsync("/");
            home.StatusCode.Should().Be(HttpStatusCode.OK);
            homeHeaders.Add(home.Headers.GetValues("Content-Security-Policy").Single());
            using var notFound = await client.GetAsync("/not-found");
            notFound.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        for (var i = 0; i < 5; i++)
        {
            using var robots = await client.GetAsync("/robots.txt");
            robots.StatusCode.Should().Be(HttpStatusCode.OK);
            using var asset = await client.GetAsync("/js/static-glue.js");
            asset.StatusCode.Should().Be(HttpStatusCode.OK, "a static asset takes the same cached header, with no document sources");
            asset.Headers.GetValues("Content-Security-Policy").Single().Should().Be(
                robots.Headers.GetValues("Content-Security-Policy").Single());
        }

        counts.AtStart.Should().Be(1, "the request contributors ran for the one start variant, not for 60 responses");
        counts.PerDocument.Should().Be(2,
            "the document contributors ran for the two kinds seen (a static page, not a document), not for 60 responses");
        homeHeaders.Distinct().Should().ContainSingle("every response of a variant carries the same header");
    }

    [Fact]
    public async Task AContributorWithoutAKey_KeepsRunningPerResponse()
    {
        var counts = new Counts();
        using var factory = CreateFactory(services =>
            services.AddSingleton<ICspContributor>(new CountingContributor(counts, key: null)));
        using var client = factory.CreateClient();

        for (var i = 0; i < 10; i++)
        {
            using var response = await client.GetAsync("/");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        counts.AtStart.Should().Be(10, "a contributor that does not say what its output depends on cannot be cached");
    }

    [Fact]
    public async Task AResponse_BuildsNoMutablePolicy_UnlessItsCodeAsksForOne()
    {
        var probe = new MaterializedProbeFilter();
        using var factory = CreateFactory(services => services.AddSingleton<IStartupFilter>(probe));
        using var client = factory.CreateClient();

        for (var i = 0; i < 6; i++)
        {
            using var response = await client.GetAsync("/");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        probe.Materialized.Should().HaveCount(6);
        probe.Materialized.Should().OnlyContain(m => !m,
            "no response builds a per-request policy: the header comes from the finished strings");
    }

    private static WebApplicationFactory<Program> CreateFactory(Action<IServiceCollection> configureServices) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("Blog:BaseUrl", "https://example.com/");
            builder.UseSetting("Analytics:IngestKey", "test-ingest-key");
            builder.UseEnvironment("Production");
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient("BlogApi").ConfigurePrimaryHttpMessageHandler(() => new StubBlogApiHandler());
                configureServices(services);
            });
        });

    private sealed class Counts
    {
        private int _atStart;
        private int _perDocument;

        public int AtStart => _atStart;

        public int PerDocument => _perDocument;

        public void StartRan() => Interlocked.Increment(ref _atStart);

        public void DocumentRan() => Interlocked.Increment(ref _perDocument);
    }

    private sealed class CountingContributor(Counts counts, string? key) : ICspContributor
    {
        public void Contribute(HttpContext context, CspPolicy policy) => counts.StartRan();

        public string? CacheKey(HttpContext context) => key;
    }

    private sealed class CountingDocumentContributor(Counts counts, string? key) : IDocumentCspContributor
    {
        public void Contribute(HttpContext context, CspPolicy policy, CspPolicy reportOnlyPolicy, CspDocumentKind kind) =>
            counts.DocumentRan();

        public string? CacheKey(HttpContext context, CspDocumentKind kind) => key;
    }

    /// <summary>Records, for each response, whether its CSP feature built the mutable policies. The
    /// filter's middleware runs before CspMiddleware, so its OnStarting callback was registered
    /// first and runs last: after the middleware's own.</summary>
    private sealed class MaterializedProbeFilter : IStartupFilter
    {
        public List<bool> Materialized { get; } = [];

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                context.Response.OnStarting(() =>
                {
                    lock (Materialized)
                    {
                        Materialized.Add(context.Features.Get<ICspFeature>() is CspFeature { IsMaterialized: true });
                    }

                    return Task.CompletedTask;
                });
                await nextMiddleware(context);
            });
            next(app);
        };
    }
}
