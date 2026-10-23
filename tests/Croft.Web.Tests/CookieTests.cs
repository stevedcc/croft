using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Croft.Web.Services;

namespace Croft.Web.Tests;

/// <summary>
/// Brief 10c item 5: cookies. A static reading page answers an anonymous visitor with NO
/// Set-Cookie (and so none of the no-store headers the antiforgery token generation adds);
/// admin pages still issue the antiforgery cookie, and outside Development every cookie the site
/// sets is Secure (TLS ends at the proxy, so "same as request" gave no Secure flag): the
/// antiforgery cookie, the sign-in cookie and the OIDC correlation and nonce cookies. A form POST
/// to an admin page without its antiforgery token is still refused.
/// </summary>
public class CookieTests
{
    private const string Authority = "https://idp.example.com";

    [Theory]
    [InlineData("/")]
    [InlineData("/categories")]
    [InlineData("/search")]
    [InlineData("/search?q=hello")]
    [InlineData("/post/no-such-post")]
    [InlineData("/category/no-such-category")]
    [InlineData("/tag/no-such-tag")]
    [InlineData("/no-such-page")]
    [InlineData("/no/such/page")]
    [InlineData("/not-found")]
    [InlineData("/Error")]
    public async Task StaticReadingPage_SetsNoCookie_ForAnAnonymousVisitor(string path)
    {
        using var factory = CreateFactory();
        using var client = Client(factory);
        using var response = await client.GetAsync(path);

        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html", path);
        SetCookies(response).Should().BeEmpty($"{path} is a static reading page: it renders no form, so it needs no antiforgery token and sets no cookie");
    }

    [Fact]
    public async Task StaticReadingPage_IsNotMarkedNoStore_ByATokenItNeverNeeded()
    {
        using var factory = CreateFactory();
        using var client = Client(factory);
        using var response = await client.GetAsync("/");

        response.Headers.CacheControl?.NoStore.Should().NotBe(true,
            "no-store was only there because the antiforgery token was stored; with no token the page may be cached (a later brief decides)");
    }

    [Fact]
    public async Task AdminPage_StillIssuesTheAntiforgeryCookie_Secure_OutsideDevelopment()
    {
        using var factory = CreateFactory(signedIn: true);
        using var client = Client(factory);
        using var response = await client.GetAsync("/admin/posts");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var antiforgery = SetCookies(response).Where(c => c.StartsWith(".AspNetCore.Antiforgery.", StringComparison.Ordinal)).ToList();
        antiforgery.Should().ContainSingle("admin pages keep antiforgery");
        antiforgery[0].Should().Contain("secure", "TLS ends at the proxy, so the cookie must say Secure whatever scheme the app saw");
        antiforgery[0].ToLowerInvariant().Should().Contain("httponly").And.Contain("samesite=strict");
    }

    [Fact]
    public async Task AFormPostToAnAdminPage_WithoutItsAntiforgeryToken_IsRefused()
    {
        using var factory = CreateFactory(signedIn: true);
        using var client = Client(factory);
        using var content = new FormUrlEncodedContent([new KeyValuePair<string, string>("x", "y")]);
        using var response = await client.PostAsync("/admin/posts", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a POST to a Razor component endpoint without a valid antiforgery token is refused by UseAntiforgery");
    }

    [Fact]
    public async Task SignInCookie_IsSecure_OutsideDevelopment()
    {
        using var factory = CreateFactory();
        var setCookie = await AuthCookie.SignInSetCookieAsync(factory.Services, "session-1");

        setCookie.ToLowerInvariant().Should().Contain("secure").And.Contain("httponly");
    }

    [Fact]
    public async Task OidcCorrelationAndNonceCookies_AreSecure_OutsideDevelopment()
    {
        using var factory = CreateFactory(oidc: true);
        using var client = Client(factory);
        using var response = await client.GetAsync("/login");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var cookies = SetCookies(response).ToList();
        cookies.Should().Contain(c => c.StartsWith(".AspNetCore.Correlation.", StringComparison.Ordinal));
        cookies.Should().Contain(c => c.StartsWith(".AspNetCore.OpenIdConnect.Nonce.", StringComparison.Ordinal));
        cookies.Should().OnlyContain(c => c.Contains("secure", StringComparison.OrdinalIgnoreCase),
            "every cookie of the sign-in redirect is Secure: the correlation and nonce cookies are SameSite=None");
    }

    private static IEnumerable<string> SetCookies(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [];

    private static HttpClient Client(WebApplicationFactory<Program> factory) =>
        // https, as the proxy's X-Forwarded-Proto makes every request look in production: with
        // CookieSecurePolicy.Always the antiforgery system refuses a request that is not SSL.
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

    private static WebApplicationFactory<Program> CreateFactory(bool signedIn = false, bool oidc = false) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("Blog:BaseUrl", "https://example.com/");
            builder.UseSetting("Analytics:IngestKey", "test-ingest-key");
            if (oidc)
            {
                builder.UseSetting("Oidc:Authority", Authority);
                builder.UseSetting("Oidc:ClientId", "blog-web");
            }

            builder.UseEnvironment("Production");
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient("BlogApi")
                    .ConfigurePrimaryHttpMessageHandler(() => new StubBlogApiHandler());
                services.AddSingleton<IUserAccessTokenService, FixedTokenService>();
                services.PostConfigureAll<OpenIdConnectOptions>(options =>
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                        new OpenIdConnectConfiguration
                        {
                            Issuer = Authority,
                            AuthorizationEndpoint = $"{Authority}/authorize",
                            EndSessionEndpoint = $"{Authority}/logout",
                        }));
                if (signedIn)
                {
                    services.AddAuthentication()
                        .AddScheme<AuthenticationSchemeOptions, AdminHandler>("TestAdmin", _ => { });
                    services.PostConfigure<AuthenticationOptions>(options =>
                    {
                        options.DefaultScheme = "TestAdmin";
                        options.DefaultAuthenticateScheme = "TestAdmin";
                    });
                }
            });
        });

    private sealed class FixedTokenService : IUserAccessTokenService
    {
        public Task<string?> GetAccessTokenAsync(ClaimsPrincipal user, CancellationToken ct = default) =>
            Task.FromResult<string?>("test-token");
    }

    private sealed class AdminHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "admin@example.com"), new Claim(ClaimTypes.Role, "Admin")], "TestAdmin");
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), "TestAdmin")));
        }
    }
}
