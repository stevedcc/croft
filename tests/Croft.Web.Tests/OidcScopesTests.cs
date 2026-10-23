using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Croft.Web.Tests;

/// <summary>
/// Brief 13d item 6 (review M-2): the scopes the sign-in asks the provider for are configuration
/// (<c>Oidc:Scopes</c>), because some providers reject a scope they do not know (Microsoft Entra
/// ID answers <c>invalid_scope</c> for <c>groups</c>). The default list is unchanged. The scope is
/// read from the authorize request the login redirect sends to the provider.
/// </summary>
public class OidcScopesTests
{
    private const string Authority = "https://idp.example.com";

    [Fact]
    public async Task WithNoSetting_TheAuthorizeRequestAsksForTheDefaultScopes()
    {
        var scope = await ScopeOfAuthorizeRequestAsync([]);

        scope.Should().Be("openid profile email groups offline_access");
    }

    [Fact]
    public async Task ConfiguredScopes_ReachTheAuthorizeRequest()
    {
        var scope = await ScopeOfAuthorizeRequestAsync(["openid", "profile", "email", "offline_access"]);

        scope.Should().Be("openid profile email offline_access", "the setting replaces the list, so groups is not asked for");
    }

    [Fact]
    public async Task ConfiguredScopesWithoutOpenid_StillAskForOpenid()
    {
        var scope = await ScopeOfAuthorizeRequestAsync(["profile", "groups"]);

        scope.Split(' ').Should().BeEquivalentTo(["openid", "profile", "groups"],
            "an OpenID Connect sign-in without the openid scope is not an OpenID Connect request");
    }

    [Fact]
    public async Task ScopesWithAGapInTheIndices_StopTheStartWithAMessageNamingTheSetting()
    {
        // Only Oidc__Scopes__1 set: index 0 is missing, so the list does not bind as written.
        var failure = await Record.ExceptionAsync(() => ScopeForIndexedSettingsAsync(
            new Dictionary<int, string> { [1] = "profile" }));

        failure.Should().NotBeNull("a scope list that silently falls back to the default is a misconfiguration");
        failure!.ToString().Should().Contain("Oidc:Scopes").And.Contain("0");
    }

    private static Task<string> ScopeOfAuthorizeRequestAsync(string[] configuredScopes) =>
        ScopeForIndexedSettingsAsync(configuredScopes.Select((scope, i) => (scope, i)).ToDictionary(x => x.i, x => x.scope));

    private static async Task<string> ScopeForIndexedSettingsAsync(Dictionary<int, string> configuredScopes)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("Blog:BaseUrl", "https://example.com/");
            builder.UseSetting("Analytics:IngestKey", "test-ingest-key");
            builder.UseSetting("Oidc:Authority", Authority);
            builder.UseSetting("Oidc:ClientId", "blog-web");
            foreach (var (index, scope) in configuredScopes)
            {
                builder.UseSetting($"Oidc:Scopes:{index}", scope);
            }

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

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync("/login");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        return QueryHelpers.ParseQuery(response.Headers.Location!.Query)["scope"].ToString();
    }
}
