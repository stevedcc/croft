using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Croft.Web.Tests;

/// <summary>
/// Brief 13a item 4: the sign-in's return target is the OIDC RedirectUri, which the framework
/// redirects to verbatim after the IdP callback; so a return URL a browser would resolve off-site
/// (a tab or newline between the slashes) must never become it. The RedirectUri is read back
/// from the state parameter of the redirect to the IdP, through the handler's own state format.
/// </summary>
public class LoginReturnUrlTests
{
    private const string Authority = "https://idp.example.com";

    [Theory]
    [InlineData("%2F%09%2Fevil.example")]
    [InlineData("%2F%0A%2F%2Fevil.example")]
    [InlineData("%2F%0D%2Fevil.example")]
    [InlineData("%2F%20%2Fevil.example")]
    [InlineData("%2F%5Cevil.example")]
    [InlineData("%2F%2Fevil.example")]
    [InlineData("https%3A%2F%2Fevil.example")]
    public async Task ALoginWithAReturnUrlThatLeavesTheSite_ReturnsToTheHomePage(string encodedReturnUrl)
    {
        using var factory = CreateFactory();

        var redirectUri = await RedirectUriOfLoginAsync(factory, encodedReturnUrl);

        redirectUri.Should().Be("/", "the sign-in must never send the browser anywhere but this site");
    }

    [Fact]
    public async Task ALoginWithALocalReturnUrl_ReturnsThere()
    {
        using var factory = CreateFactory();

        var redirectUri = await RedirectUriOfLoginAsync(factory, "%2Fadmin%2Fposts%3Fpage%3D2");

        redirectUri.Should().Be("/admin/posts?page=2");
    }

    private static async Task<string?> RedirectUriOfLoginAsync(WebApplicationFactory<Program> factory, string encodedReturnUrl)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync($"/login?returnUrl={encodedReturnUrl}");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var state = QueryHelpers.ParseQuery(response.Headers.Location!.Query)["state"].ToString();
        var options = factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OpenIdConnectDefaults.AuthenticationScheme);
        return options.StateDataFormat.Unprotect(state)!.RedirectUri;
    }

    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("Blog:BaseUrl", "https://example.com/");
            builder.UseSetting("Analytics:IngestKey", "test-ingest-key");
            builder.UseSetting("Oidc:Authority", Authority);
            builder.UseSetting("Oidc:ClientId", "blog-web");
            builder.UseEnvironment("Production");
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient("BlogApi").ConfigurePrimaryHttpMessageHandler(() => new StubBlogApiHandler());
                services.PostConfigureAll<OpenIdConnectOptions>(options =>
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                        new OpenIdConnectConfiguration
                        {
                            Issuer = Authority,
                            AuthorizationEndpoint = $"{Authority}/authorize",
                            EndSessionEndpoint = $"{Authority}/logout",
                        }));
            });
        });
}
