using System.Net;
using System.Text.RegularExpressions;
using Croft.Web.Services;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Croft.Web.Tests;

/// <summary>
/// Sign-out clears every local draft backup (a shared computer keeps no drafts after sign-out)
/// without breaking the sign-out flow: GET /logout redirects to the small interstitial page,
/// which carries an antiforgery token in a POST form; the interstitial's same-origin script clears
/// the `croft-draft:` keys and submits the form to /logout/complete, which runs the cookie + IdP
/// sign-out only for a POST with a valid token (brief 13b: a cross-site link must not sign a staff
/// member out). The interstitial is CSP-clean — an external script and no inline style attribute —
/// because the enforced policy allows nothing else.
/// </summary>
public class SignOutInterstitialTests
{
    private const string Authority = "https://idp.example.com";
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Interstitial_HasAPostFormWithAToken_AVisibleSubmitButton_AndTheSameOriginScriptOnly()
    {
        using var factory = CreateFactory();
        var (html, _) = await GetInterstitialAsync(factory);

        html.Should().Contain("<form method=\"post\" action=\"/logout/complete\"",
            "the sign-out is a POST, so a cross-site link cannot trigger it");
        html.Should().MatchRegex("<input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"[^\"]{20,}\"");
        html.Should().MatchRegex("<button type=\"submit\"[^>]*>Continue signing out</button>",
            "without JavaScript the user finishes the sign-out by hand");
        html.Should().Contain("<script src=\"/js/logout.js\"></script>",
            "the draft-clearing runs from a same-origin script file, allowed by CSP");
        html.Should().NotMatchRegex("<script(?![^>]*\\bsrc=)", "the interstitial carries no inline script");
        html.Should().NotContain("style=", "the interstitial carries no inline style attribute");
        html.Should().Contain("<meta name=\"robots\" content=\"noindex\">", "a transient sign-out page belongs in no search index");
    }

    [Fact]
    public async Task Interstitial_RendersAFreshTokenPerRequest()
    {
        using var factory = CreateFactory();
        var (first, _) = await GetInterstitialAsync(factory);
        var (second, _) = await GetInterstitialAsync(factory);

        ExtractToken(first).Should().NotBe(ExtractToken(second),
            "the page is rendered per request, not served as a constant string");
    }

    [Fact]
    public async Task Logout_RedirectsToTheInterstitial()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync("/logout");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect,
            "the logout entry point answers a redirect, as it always has");
        response.Headers.Location!.ToString().Should().Be("/logout/interstitial");
    }

    [Fact]
    public async Task Interstitial_IsServedWithItsScript()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync("/logout/interstitial");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        (await response.Content.ReadAsStringAsync()).Should().Contain("/js/logout.js");
    }

    [Fact]
    public async Task GetLogoutComplete_DoesNotSignOut_AndAnswers405()
    {
        using var factory = CreateFactory();
        var (store, cookie) = await SignInAsync(factory);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/logout/complete");
        request.Headers.Add("Cookie", cookie);
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed,
            "a cross-site link (a GET) must not end a staff member's session");
        (await store.GetAsync("s-csrf")).Should().NotBeNull("the session is still in the store");
        (await AuthCookie.AuthenticateAsync(factory.Services, cookie)).Succeeded.Should().BeTrue("the cookie is still valid");
    }

    [Fact]
    public async Task PostLogoutComplete_WithoutAToken_Returns400_AndKeepsTheSession()
    {
        using var factory = CreateFactory();
        var (store, cookie) = await SignInAsync(factory);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/logout/complete")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>()),
        };
        request.Headers.Add("Cookie", cookie);
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await store.GetAsync("s-csrf")).Should().NotBeNull("a forged POST changes nothing");
    }

    [Fact]
    public async Task PostLogoutComplete_WithAWrongToken_Returns400_AndKeepsTheSession()
    {
        using var factory = CreateFactory();
        var (store, cookie) = await SignInAsync(factory);
        var (_, antiforgeryCookie) = await GetInterstitialAsync(factory, cookie);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/logout/complete")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = "not-a-token" }),
        };
        request.Headers.Add("Cookie", $"{cookie}; {antiforgeryCookie}");
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await store.GetAsync("s-csrf")).Should().NotBeNull("a wrong token changes nothing");
    }

    [Fact]
    public async Task PostLogoutComplete_WithTheTokenFromTheInterstitial_RemovesTheSession_AndAnswersA200PageThatContinuesToTheIdP()
    {
        using var factory = CreateFactory();
        var (store, cookie) = await SignInAsync(factory);
        var (html, antiforgeryCookie) = await GetInterstitialAsync(factory, cookie);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/logout/complete")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = ExtractToken(html) }),
        };
        request.Headers.Add("Cookie", $"{cookie}; {antiforgeryCookie}");
        using var response = await client.SendAsync(request);

        // A 200, not a 302: Chromium would block a form POST's redirect to the IdP under form-action 'self' (W-096).
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Location.Should().BeNull();
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        (await store.GetAsync("s-csrf")).Should().BeNull("the store entry was removed before the page was served");
        response.Headers.GetValues("Set-Cookie").Should().Contain(header => header.StartsWith(".AspNetCore.Cookies=;", StringComparison.Ordinal),
            "the auth cookie is cleared");

        var body = await response.Content.ReadAsStringAsync();
        var refresh = Regex.Match(body, "<meta http-equiv=\"refresh\" content=\"0;url=([^\"]+)\"").Groups[1].Value;
        var link = Regex.Match(body, "<a href=\"([^\"]+)\">Continue signing out</a>").Groups[1].Value;
        var endSession = WebUtility.HtmlDecode(refresh);
        endSession.Should().StartWith($"{Authority}/logout").And.Contain("id_token_hint=the-id-token");
        refresh.Should().Contain("&amp;", "the URL is HTML-encoded in the attribute");
        link.Should().Be(refresh, "the visible link goes to the same URL as the refresh");
        body.Should().NotContain("<script").And.NotContain("style=");
    }

    /// <summary>
    /// Pins a ruling: COEP goes on documents as the CSP decides them (CspDocument.KindOf, a Razor
    /// component page), not on every text/html response. The two sign-out pages are HTML served by
    /// minimal APIs, so they carry none; a change that keys COEP on the content type fails here.
    /// </summary>
    [Fact]
    public async Task SignOutPages_AreHtmlButNotComponentDocuments_SoTheyCarryNoCoep()
    {
        using var factory = CreateFactory();
        var (_, cookie) = await SignInAsync(factory);
        var (html, antiforgeryCookie) = await GetInterstitialAsync(factory, cookie);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

        using var interstitial = await client.GetAsync("/logout/interstitial");
        using var completeRequest = new HttpRequestMessage(HttpMethod.Post, "/logout/complete")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = ExtractToken(html) }),
        };
        completeRequest.Headers.Add("Cookie", $"{cookie}; {antiforgeryCookie}");
        using var complete = await client.SendAsync(completeRequest);

        foreach (var (response, page) in new[] { (interstitial, "the interstitial"), (complete, "the sign-out continuation page") })
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"{page} is served");
            response.Content.Headers.ContentType!.MediaType.Should().Be("text/html", $"{page} is HTML");
            response.Headers.Contains("Cross-Origin-Embedder-Policy").Should().BeFalse(
                $"{page} is not a component document by CspDocument.KindOf, the rule COEP follows");
        }
    }

    [Fact]
    public async Task Interstitial_IsNotCached()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync("/logout/interstitial");

        response.Headers.CacheControl!.NoStore.Should().BeTrue("the page carries a token");
    }

    private static async Task<(ITokenStore Store, string Cookie)> SignInAsync(WebApplicationFactory<Program> factory)
    {
        var store = factory.Services.GetRequiredService<ITokenStore>();
        await store.SetAsync("s-csrf", new TokenEntry("at", "rt", Now.AddHours(1), IdToken: "the-id-token"));
        return (store, await AuthCookie.SignInAsync(factory.Services, "s-csrf", "Admin"));
    }

    private static async Task<(string Html, string AntiforgeryCookie)> GetInterstitialAsync(
        WebApplicationFactory<Program> factory, string? authCookie = null)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/logout/interstitial");
        if (authCookie is not null)
        {
            request.Headers.Add("Cookie", authCookie);
        }

        using var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var cookie = response.Headers.GetValues("Set-Cookie")
            .Select(header => header.Split(';')[0])
            .Single(pair => pair.StartsWith(".AspNetCore.Antiforgery", StringComparison.Ordinal));
        return (await response.Content.ReadAsStringAsync(), cookie);
    }

    private static string ExtractToken(string html) =>
        Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;

    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("Blog:BaseUrl", "https://example.com/");
            builder.UseSetting("Analytics:IngestKey", "test-ingest-key");
            builder.UseSetting("Oidc:Authority", Authority);
            builder.UseSetting("Oidc:ClientId", "blog-web");
            builder.ConfigureServices(services =>
                services.PostConfigureAll<OpenIdConnectOptions>(options =>
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                        new OpenIdConnectConfiguration
                        {
                            Issuer = Authority,
                            AuthorizationEndpoint = $"{Authority}/authorize",
                            EndSessionEndpoint = $"{Authority}/logout",
                        })));
        });
}
