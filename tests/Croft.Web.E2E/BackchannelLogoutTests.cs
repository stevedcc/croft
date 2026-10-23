using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Croft.Web.E2E;

/// <summary>
/// OIDC Back-Channel Logout 1.0: when the IdP ends a session it POSTs a signed logout_token to
/// the blog, and the blog session must end with it. The mock IdP really signs the token — its
/// interactive login accepts a claims document the way dev/login.html supplies identity claims,
/// so the id_token it mints is a valid logout token that the blog verifies against the mock's
/// JWKS (the production validation path).
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class BackchannelLogoutTests : WebTest
{
    private const string RedirectUri = "http://localhost:9999/cb";

    [Test]
    public async Task BackChannelLogoutToken_EndsTheBlogSession()
    {
        // A subject of its own: the token logs out every session of that user, so it must not
        // touch sessions other tests create.
        const string user = "backchannel-logout@example.com";
        await LoginAsync(user, "password");

        var logoutToken = await MintLogoutTokenAsync(user);

        using var http = new HttpClient();
        var response = await http.PostAsync($"{BaseUrl}/backchannel-logout",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["logout_token"] = logoutToken }));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            "A validly signed logout token must be accepted.");

        // Outcome: the session is over server-side, so the next request's cookie is rejected and
        // the shell is the logged-out one again ("Log out" gone, "Log in" back).
        await Page.ReloadAsync();
        await Expect(Page.Locator("#sidebar-nav a[href='/logout']")).ToHaveCountAsync(0);
        await Expect(Page.Locator("#sidebar-nav a[href='/login']")).ToBeVisibleAsync();
    }

    /// <summary>
    /// Runs the mock IdP's interactive login over HTTP — a username and a claims JSON document,
    /// exactly what dev/login.html posts — and returns the signed id_token issued for the
    /// subject, carrying the back-channel logout event. Used as the logout token.
    /// </summary>
    private static async Task<string> MintLogoutTokenAsync(string subject)
    {
        // No redirect following: the mock answers the login with a 302 whose Location carries
        // the authorization code — the redirect target (any URI) is never contacted.
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var authorizeUrl = $"{OAuthAuthority}/authorize" +
            "?client_id=blog-web&redirect_uri=" + RedirectUri + "&response_type=code&scope=openid&state=x";

        await http.GetAsync(authorizeUrl);
        using var login = await http.PostAsync(authorizeUrl, new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["username"] = subject,
                ["claims"] = JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["sub"] = subject,
                    ["jti"] = "backchannel-logout-e2e-1",
                    ["events"] = new Dictionary<string, object>
                    {
                        ["http://schemas.openid.net/event/backchannel-logout"] = new Dictionary<string, object>()
                    }
                })
            }));
        var location = login.Headers.Location?.ToString() ?? login.Headers.ToString();
        var code = Regex.Match(location, @"[?&]code=([^&]+)").Groups[1].Value;
        Assert.That(code, Is.Not.Empty, $"The mock IdP returned no authorization code: {location}");

        using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, $"{OAuthAuthority}/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = RedirectUri,
                ["client_id"] = "blog-web"
            })
        };
        tokenRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("blog-web:secret")));
        using var tokenResponse = await http.SendAsync(tokenRequest);
        Assert.That(tokenResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            "The mock IdP did not exchange the authorization code.");
        using var body = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id_token").GetString()!;
    }
}
