using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Croft.Web.Tests;

/// <summary>
/// The tokens never live in the cookie (AGENTS.md, "Auth tokens in the Web app"): the sign-in keeps
/// them in the server-side store under a random session id, and the cookie holds claims only.
/// ASP.NET Core would put the access, refresh and ID tokens into the data-protected cookie if
/// <c>OpenIdConnectOptions.SaveTokens</c> were true, with every other test green: this pins the
/// option on the app's own configuration (review 2026-10-19, I-9).
/// </summary>
public class OidcSaveTokensTests
{
    [Fact]
    public void TheOpenIdConnectScheme_DoesNotSaveTokensIntoTheCookie()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("Blog:BaseUrl", "https://example.com/");
            builder.UseSetting("Analytics:IngestKey", "test-ingest-key");
            builder.UseSetting("Oidc:Authority", "https://idp.example.com");
            builder.UseSetting("Oidc:ClientId", "blog-web");
            builder.UseEnvironment("Production");
        });

        var options = factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OpenIdConnectDefaults.AuthenticationScheme);

        options.SaveTokens.Should().BeFalse(
            "SaveTokens would store the access, refresh and ID tokens in the authentication cookie");
    }
}
