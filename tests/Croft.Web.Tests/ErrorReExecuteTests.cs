using System.Net;
using Croft.Web.Tests.Fixtures;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Croft.Web.Tests;

/// <summary>
/// Brief 13a review: outside Development an unhandled exception re-executes /Error. That page is
/// a static document now, so the 500 it answers carries the error copy, the static policy and no
/// cookie (no antiforgery token, no circuit).
/// </summary>
public class ErrorReExecuteTests
{
    private const string Authority = "https://idp.example.com";

    [Fact]
    public async Task AnUnhandledException_ReExecutesTheStaticErrorPage_With500_TheStaticPolicy_AndNoCookie()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("Blog:BaseUrl", "https://example.com/");
            builder.UseSetting("Analytics:IngestKey", "test-ingest-key");
            builder.UseSetting("Oidc:Authority", Authority);
            builder.UseSetting("Oidc:ClientId", "blog-web");
            builder.UseEnvironment("Production");
            builder.ConfigureServices(services =>
            {
                // The first API call throws (the page fails with an unhandled exception); the calls of
                // the re-executed /Error request, whose layout reads the API too, are answered.
                services.AddHttpClient("BlogApi").ConfigurePrimaryHttpMessageHandler(() => new ThrowOnceHandler());
                services.PostConfigureAll<OpenIdConnectOptions>(options =>
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                        new OpenIdConnectConfiguration { Issuer = Authority, AuthorizationEndpoint = $"{Authority}/authorize" }));
            });
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync("/categories");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError, "the exception handler keeps the failure status");
        html.Should().Contain("Something went wrong", "the error page's copy is what is served");
        BlazorCircuitClient.CollectSsrComponentMarkers(html).Should().Be("[]", "the error page opens no circuit");
        response.Headers.GetValues("Content-Security-Policy").Should().ContainSingle().Which
            .Should().Be(CspSnapshots.For("error page"), "the re-executed page carries the static policy");
        response.Headers.Contains("Set-Cookie").Should().BeFalse("a static document sets no cookie");
    }

    private sealed class ThrowOnceHandler : HttpMessageHandler
    {
        private readonly StubBlogApiHandler _stub = new();
        private int _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
                throw new InvalidOperationException("the API is down (test)");

            return new HttpMessageInvoker(_stub).SendAsync(request, cancellationToken);
        }
    }
}
