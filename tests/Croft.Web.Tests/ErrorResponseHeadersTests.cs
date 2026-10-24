using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Croft.Web.Tests;

/// <summary>
/// Brief 13h item 1: the responses the exception handler answers with (the /Error page's 500, and
/// the 503 of an API outage) carry every plain security header and the error page's CSP, each
/// exactly once. UseExceptionHandler clears the response, headers included, before it writes.
/// </summary>
public class ErrorResponseHeadersTests
{
    private const string Authority = "https://idp.example.com";

    [Fact]
    public async Task APageThatThrows_AnsweredWith500ByTheErrorPage_CarriesEveryHeaderOnce()
    {
        using var factory = CreateFactory(new ThrowOnceHandler());
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync("/categories");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        AssertEveryHeaderOnce(response);
    }

    [Fact]
    public async Task AnApiOutage_Answered503ByTheErrorPage_CarriesEveryHeaderOnce()
    {
        var api = new ScriptedApiHandler((_, _) => ScriptedApiHandler.ConnectionError);
        using var factory = CreateFactory(api);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync("/categories");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        AssertEveryHeaderOnce(response);
    }

    private static WebApplicationFactory<Program> CreateFactory(HttpMessageHandler api) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("Blog:BaseUrl", "https://example.com/");
            builder.UseSetting("Analytics:IngestKey", "test-ingest-key");
            builder.UseSetting("Oidc:Authority", Authority);
            builder.UseSetting("Oidc:ClientId", "blog-web");
            builder.UseSetting("Croft:Web:Api:AttemptTimeoutSeconds", "0.2");
            builder.UseSetting("Croft:Web:Api:TotalTimeoutSeconds", "0.5");
            builder.UseSetting("Croft:Web:Api:RetryDelayMilliseconds", "1");
            builder.UseEnvironment("Production");
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient("BlogApi").ConfigurePrimaryHttpMessageHandler(() => api);
                services.PostConfigureAll<OpenIdConnectOptions>(options =>
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                        new OpenIdConnectConfiguration { Issuer = Authority, AuthorizationEndpoint = $"{Authority}/authorize" }));
            });
        });

    private static void AssertEveryHeaderOnce(HttpResponseMessage response)
    {
        AssertOnce(response, "Strict-Transport-Security", "max-age=31536000; includeSubDomains");
        AssertOnce(response, "X-Content-Type-Options", "nosniff");
        AssertOnce(response, "Referrer-Policy", "strict-origin-when-cross-origin");
        AssertOnce(response, "Permissions-Policy", "camera=(), microphone=(), geolocation=()");
        AssertOnce(response, "Cross-Origin-Opener-Policy", "same-origin");
        AssertOnce(response, "Cross-Origin-Embedder-Policy", "require-corp");
        AssertOnce(response, "Content-Security-Policy", CspSnapshots.For("error page"));
        AssertOnce(response, "Reporting-Endpoints", "csp=\"/csp-report\"");
    }

    private static void AssertOnce(HttpResponseMessage response, string name, string value)
    {
        response.Headers.Contains(name).Should().BeTrue($"the error response carries {name}");
        response.Headers.GetValues(name).Should().ContainSingle($"{name} appears exactly once").Which.Should().Be(value);
    }

    private sealed class ThrowOnceHandler : HttpMessageHandler
    {
        private readonly StubBlogApiHandler _stub = new();
        private int _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
                throw new InvalidOperationException("the page fails (test)");

            return new HttpMessageInvoker(_stub).SendAsync(request, cancellationToken);
        }
    }
}
