using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Croft.Web.Services;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Croft.Web.Tests;

/// <summary>
/// The Content-Security-Policy of each kind of document, as the complete header string, against
/// the text in CspSnapshots.txt. A change to the policy must change a snapshot on purpose: the
/// file says that changing it is an owner decision if it loosens anything. The kinds: a static
/// reading page, the not-found and error pages (static documents since brief 13a: anonymous visitors never open a circuit), an admin page,
/// the post editor, the staff drafts page, the login redirect (not a document) and the sign-out
/// interstitial (a small page with one script).
/// </summary>
public class CspPolicySnapshotTests
{
    private const string Authority = "https://idp.example.com";

    [Theory]
    [InlineData("static reading page", "/", HttpStatusCode.OK, false)]
    [InlineData("not-found page", "/not-found", HttpStatusCode.OK, false)]
    [InlineData("not-found page", "/no/such/page", HttpStatusCode.NotFound, false)]
    [InlineData("error page", "/Error", HttpStatusCode.OK, false)]
    [InlineData("admin page", "/admin/posts", HttpStatusCode.OK, true)]
    [InlineData("post editor", "/admin/posts/new", HttpStatusCode.OK, true)]
    [InlineData("staff drafts page", "/editor", HttpStatusCode.OK, true)]
    [InlineData("login redirect", "/login", HttpStatusCode.Redirect, false)]
    [InlineData("sign-out interstitial", "/logout/interstitial", HttpStatusCode.OK, false)]
    public async Task EveryDocumentKind_ServesExactlyItsSnapshotPolicy(
        string kind, string path, HttpStatusCode status, bool signedIn)
    {
        using var factory = CreateFactory(signedIn);
        // https, as behind the ingress: the sign-out interstitial renders an antiforgery token,
        // and the Secure-always cookie policy refuses to issue one over plain http.
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });
        using var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(status, kind);
        response.Headers.GetValues("Content-Security-Policy").Should().ContainSingle(
            "one middleware writes one enforcing header").Which
            .Should().Be(CspSnapshots.For(kind), $"the {kind}'s policy is pinned by CspSnapshots.txt");
    }

    [Theory]
    [InlineData("static reading page", "/", false, true)]
    [InlineData("not-found page", "/not-found", false, true)]
    [InlineData("admin page", "/admin/posts", true, false)]
    [InlineData("login redirect", "/login", false, false)]
    public async Task OnlyAStaticReadingPage_ShipsTrustedTypes_AndOnlyInReportOnly(
        string kind, string path, bool signedIn, bool expected)
    {
        // blazor.web.js (enhanced navigation) violates require-trusted-types-for on every
        // reading page (10c item 0), so the directives are reported, not enforced, and the
        // enforcing header never carries them.
        using var factory = CreateFactory(signedIn);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync(path);

        response.Headers.GetValues("Content-Security-Policy").Single().Should().NotContain("trusted-types",
            $"the {kind}'s enforcing policy must not carry Trusted Types while blazor.web.js violates it");
        if (expected)
        {
            response.Headers.GetValues("Content-Security-Policy-Report-Only").Should().ContainSingle().Which
                .Should().Be(CspSnapshots.For("static reading page, report-only"));
        }
        else
        {
            response.Headers.Contains("Content-Security-Policy-Report-Only").Should().BeFalse(
                $"the {kind} has no Trusted Types report-only policy (only the measured reading pages do)");
        }
    }

    private static WebApplicationFactory<Program> CreateFactory(bool signedIn) =>
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
                services.AddHttpClient("BlogApi")
                    .ConfigurePrimaryHttpMessageHandler(() => new StubBlogApiHandler());
                services.AddSingleton<IUserAccessTokenService, FixedTokenService>();

                // The login redirect must not fetch the IdP's discovery document.
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

    /// <summary>Signs every request in as an admin, so the staff pages render as documents.</summary>
    private sealed class AdminHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "admin@example.com"), new Claim(ClaimTypes.Role, "Admin")],
                "TestAdmin");
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), "TestAdmin")));
        }
    }
}

/// <summary>Reads CspSnapshots.txt: "[kind]" headers, each followed by the policy on one line;
/// lines starting with # are comments.</summary>
internal static class CspSnapshots
{
    public static string For(string kind)
    {
        var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "CspSnapshots.txt"))
            .Where(line => !line.StartsWith('#') && line.Length > 0)
            .ToList();
        var header = lines.IndexOf($"[{kind}]");
        header.Should().BeGreaterThanOrEqualTo(0, $"CspSnapshots.txt has a [{kind}] section");
        return lines[header + 1];
    }
}
