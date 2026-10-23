using System.Net;
using System.Security.Cryptography;
using System.Text;
using Croft.Web.Security.Csp;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests;

/// <summary>
/// The Web app's composable CSP: one middleware writes exactly one header per response, the
/// value is the base policy merged with the site's configured additions, the built-in
/// contributors and anything a component or endpoint adds for this request through the
/// ICspFeature hook (which must not leak onto the next response). Csp:ReportOnly (default
/// false: enforced) picks between the enforcing and the report-only header name, and Blazor's own
/// frame-ancestors header is disabled so ours is the only CSP writer.
/// </summary>
public class CspHeaderTests
{
    // The hash source that allows App.razor's inline theme bootstrap script on every
    // response, derived from the one source of truth (the constant App.razor renders and
    // ScriptSourcesCspContributor hashes). That the hash matches the RENDERED script
    // element's text is ThemeBootstrapScriptCspTests' assertion, not this file's.
    private static readonly string ThemeScriptSource =
        $"'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(ThemeBootstrapScript.Text)))}'";

    // The policy of a static reading page ("/") with nothing configured. img-src has no data:
    // source on purpose: no markup, stylesheet or script of the app or of MudBlazor loads a data:
    // image (MudBlazor's only data: images are CSS backgrounds of its colour picker, which
    // nothing renders). style-src-elem carries no hash: a reading page renders no MudThemeProvider
    // and so no inline style block (10c item 1; the interactive documents' hashes are
    // MudThemeStyleCspTests' assertion).
    private static Task<string> BasePolicyAsync(WebApplicationFactory<Program> factory) =>
        Task.FromResult(
            $"default-src 'none'; script-src {ThemeScriptSource} https://example.com/_framework/ " +
            "https://example.com/js/ https://example.com/lib/; style-src 'self'; " +
            "style-src-elem 'self'; " +
            "style-src-attr 'none'; img-src 'self'; font-src 'self'; connect-src 'self'; " +
            "manifest-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; " +
            "frame-ancestors 'none'; frame-src 'none'; worker-src 'none'; " +
            "report-uri /csp-report; report-to csp");

    // In report-only mode the whole policy is report-only already, so the reading pages'
    // Trusted Types directives (10c item 4) join the one header, before the report endpoints.
    private static async Task<string> ReportOnlyModePolicyAsync(WebApplicationFactory<Program> factory) =>
        (await BasePolicyAsync(factory)).Replace("worker-src 'none'; ",
            "worker-src 'none'; require-trusted-types-for 'script'; trusted-types 'none'; ");

    private const string TrustedTypesReportOnly =
        "require-trusted-types-for 'script'; trusted-types 'none'; report-uri /csp-report; report-to csp";

    [Fact]
    public async Task PageResponse_CarriesExactlyOneCspHeader_WithTheBasePolicy()
    {
        using var factory = CreateFactory(new() { ["Csp:ReportOnly"] = "true" });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("Content-Security-Policy-Report-Only").Should().ContainSingle(
            "one middleware writes one header").Which.Should().Be(await ReportOnlyModePolicyAsync(factory));
        response.Headers.Contains("Content-Security-Policy").Should().BeFalse(
            "Blazor's own frame-ancestors header is disabled; our policy is the only CSP writer");
        response.Headers.GetValues("Reporting-Endpoints").Should().ContainSingle(
            "the report-to group is named here").Which.Should().Be("csp=\"/csp-report\"");
    }

    [Fact]
    public async Task ReportOnly_TrueWritesTheReportOnlyHeaderName()
    {
        using var factory = CreateFactory(new() { ["Csp:ReportOnly"] = "true" });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/");

        response.Headers.GetValues("Content-Security-Policy-Report-Only").Should().ContainSingle()
            .Which.Should().Be(await ReportOnlyModePolicyAsync(factory));
        response.Headers.Contains("Content-Security-Policy").Should().BeFalse(
            "report-only mode — the rollout's first step — must not also send the enforcing header");
    }

    [Fact]
    public async Task ReportOnly_DefaultWritesTheEnforcingHeaderName()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/");

        response.Headers.GetValues("Content-Security-Policy").Should().ContainSingle(
            "one middleware writes one header").Which.Should().Be(await BasePolicyAsync(factory));
        response.Headers.GetValues("Content-Security-Policy-Report-Only").Should().ContainSingle(
            "the only report-only policy on an enforced site is the reading pages' Trusted Types").Which
            .Should().Be(TrustedTypesReportOnly);
    }

    [Fact]
    public async Task ReportOnly_FalseWritesTheEnforcingHeaderName()
    {
        using var factory = CreateFactory(new() { ["Csp:ReportOnly"] = "false" });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/");

        response.Headers.GetValues("Content-Security-Policy").Should().ContainSingle(
            "one middleware writes one header").Which.Should().Be(await BasePolicyAsync(factory));
        response.Headers.GetValues("Content-Security-Policy-Report-Only").Should().ContainSingle(
            "enforced mode sends no report-only policy but the reading pages' Trusted Types").Which
            .Should().Be(TrustedTypesReportOnly);
    }

    [Fact]
    public async Task ConfigAdditions_MergeIntoThePolicy_WithoutRemovingTheBase()
    {
        using var factory = CreateFactory(new()
        {
            ["Csp:ReportOnly"] = "true",
            ["Csp:Add:img-src:0"] = "https://cdn.example.com",
            ["Csp:Add:img-src:1"] = "https://images.example.com",
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/");

        var expected = (await ReportOnlyModePolicyAsync(factory)).Replace(
            "img-src 'self';", "img-src 'self' https://cdn.example.com https://images.example.com;");
        response.Headers.GetValues("Content-Security-Policy-Report-Only").Should().ContainSingle()
            .Which.Should().Be(expected, "the site's Csp:Add sources merge beside the base ones, never replace them");
    }

    [Fact]
    public async Task PerRequestAddition_AppearsOnlyOnThatResponse()
    {
        using var factory = CreateFactory(new() { ["Csp:ReportOnly"] = "true" }, startupFilter: new FrameSourceProbeFilter());
        using var client = factory.CreateClient();

        using var probe = await client.GetAsync("/csp-feature-probe");
        probe.Headers.GetValues("Content-Security-Policy-Report-Only").Should().ContainSingle().Which
            .Should().Contain("frame-src https://video.example.com",
                "the request's ICspFeature hook added a frame source before the response started");

        using var home = await client.GetAsync("/");
        home.StatusCode.Should().Be(HttpStatusCode.OK);
        home.Headers.GetValues("Content-Security-Policy-Report-Only").Should().ContainSingle().Which
            .Should().NotContain("video.example.com",
                "the addition belongs to one request's policy and must not leak onto the next response");
    }

    [Fact]
    public async Task Contributors_RunPerRequest_AndCanAddOnlyForSomePaths()
    {
        using var factory = CreateFactory(new() { ["Csp:ReportOnly"] = "true" }, configureServices: services =>
            services.AddSingleton<ICspContributor, TelemetryOnlyOnProbePathContributor>());
        using var client = factory.CreateClient();

        using var probe = await client.GetAsync("/csp-feature-probe");
        probe.Headers.GetValues("Content-Security-Policy-Report-Only").Should().ContainSingle().Which
            .Should().Contain("connect-src 'self' https://telemetry.example.com",
                "the contributor sees each request and adds its source where it applies");

        using var home = await client.GetAsync("/");
        home.Headers.GetValues("Content-Security-Policy-Report-Only").Should().ContainSingle().Which
            .Should().NotContain("telemetry.example.com", "a path-scoped contribution stays on its path");
    }

    [Fact]
    public async Task OidcAuthority_DoesNotWidenFormAction()
    {
        using var factory = CreateFactory(new()
        {
            ["Csp:ReportOnly"] = "true",
            ["Oidc:Authority"] = "https://idp.example.com/realms/blog",
            ["Oidc:ClientId"] = "blog-web",
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/");

        response.Headers.GetValues("Content-Security-Policy-Report-Only").Should().ContainSingle().Which
            .Should().Contain("form-action 'self';").And.NotContain("idp.example.com",
                "sign-in is a 302 challenge and sign-out a GET link chain: no form of ours posts to the IdP (owner decision, 10c)");
    }

    private static WebApplicationFactory<Program> CreateFactory(
        Dictionary<string, string?>? settings = null,
        IStartupFilter? startupFilter = null,
        Action<IServiceCollection>? configureServices = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("Blog:BaseUrl", "https://example.com/");
            // Outside Development the app refuses to start without the analytics ingest key.
            builder.UseSetting("Analytics:IngestKey", "test-ingest-key");
            builder.UseEnvironment("Production");
            if (settings is not null)
            {
                foreach (var (key, value) in settings)
                {
                    builder.UseSetting(key, value);
                }
            }

            builder.ConfigureServices(services =>
            {
                services.AddHttpClient("BlogApi")
                    .ConfigurePrimaryHttpMessageHandler(() => new StubBlogApiHandler());
                if (startupFilter is not null)
                {
                    services.AddSingleton<IStartupFilter>(startupFilter);
                }

                configureServices?.Invoke(services);
            });
        });

    // WORKAROUND(W-078, ASP.NET Core): the probe waits for OnStarting callbacks because a startup filter runs before CspMiddleware creates the feature
    /// <summary>
    /// A test-only stand-in for a component or endpoint that needs one extra source on one
    /// response: it adds frame-src to this request's policy through the ICspFeature hook. The
    /// filter's middleware runs before CspMiddleware has created the feature, so the addition
    /// waits for the OnStarting callbacks — which still run before the response starts.
    /// </summary>
    private sealed class FrameSourceProbeFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, next) =>
            {
                if (context.Request.Path == "/csp-feature-probe")
                {
                    context.Response.OnStarting(() =>
                    {
                        context.Features.Get<ICspFeature>()!.Policy
                            .Add("frame-src", "https://video.example.com");
                        return Task.CompletedTask;
                    });
                }

                await next(context);
            });
            next(app);
        };
    }
    // END WORKAROUND(W-078)

    /// <summary>A contributor that applies to one path only, to pin that contributors run per request.</summary>
    private sealed class TelemetryOnlyOnProbePathContributor : ICspContributor
    {
        public void Contribute(HttpContext context, CspPolicy policy)
        {
            if (context.Request.Path == "/csp-feature-probe")
            {
                policy.Add("connect-src", "https://telemetry.example.com");
            }
        }
    }
}
