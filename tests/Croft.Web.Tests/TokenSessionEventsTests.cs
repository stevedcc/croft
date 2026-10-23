using System.Security.Claims;
using Croft.Shared;
using Croft.Web.Authentication;
using Croft.Web.Options;
using Croft.Web.Services;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Croft.Web.Tests;

public class TokenSessionEventsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OnTokenValidated_AddsBlogSidClaim()
    {
        var identity = new ClaimsIdentity([new Claim("sub", "admin-id")], "test");
        var context = NewContext(identity);

        await TokenSessionEvents.OnTokenValidated(context);

        var sid = identity.FindFirst("blog_sid");
        sid.Should().NotBeNull("a login session needs a session id claim for the token store");
        sid!.Value.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task OnTokenValidated_GeneratesDistinctSessionIdsPerLogin()
    {
        var first = new ClaimsIdentity([new Claim("sub", "admin-id")], "test");
        var second = new ClaimsIdentity([new Claim("sub", "admin-id")], "test");

        await TokenSessionEvents.OnTokenValidated(NewContext(first));
        await TokenSessionEvents.OnTokenValidated(NewContext(second));

        first.FindFirst("blog_sid")!.Value.Should().NotBe(second.FindFirst("blog_sid")!.Value,
            "two logins must not share a session id");
    }

    [Fact]
    public async Task OnTokenValidated_RefreshExpiresInInTheResponse_SetsTheSessionExpiry()
    {
        var now = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        // The store shares the test clock: it treats a session past its expiry as missing.
        var store = new InMemoryTokenStore(new FakeTimeProvider(now));
        var identity = new ClaimsIdentity([new Claim("sub", "admin-id")], "test");
        var response = new OpenIdConnectMessage { AccessToken = "at-1", RefreshToken = "rt-1", ExpiresIn = "300" };
        response.SetParameter("refresh_expires_in", "604800");

        await TokenSessionEvents.OnTokenValidated(NewContext(identity, response, store, now));

        var entry = await store.GetAsync(identity.FindFirst("blog_sid")!.Value);
        entry!.SessionExpiresAt.Should().Be(now.AddDays(7),
            "the session lives as long as the refresh token the IdP says it issued, not as long as the 5-minute access token");
        entry.ExpiresAt.Should().Be(now.AddSeconds(300));
    }

    [Fact]
    public async Task OnTokenValidated_DoesNotAddAccessTokenClaim()
    {
        var identity = new ClaimsIdentity([new Claim("sub", "admin-id")], "test");
        var context = NewContext(identity, new OpenIdConnectMessage { AccessToken = "the-access-token" });

        await TokenSessionEvents.OnTokenValidated(context);

        identity.FindFirst("access_token").Should().BeNull(
            "the access token must live in the server-side store, not in the cookie");
    }

    [Fact]
    public async Task OnTokenValidated_PutsNoTokenInTheAuthenticationProperties()
    {
        // ASP.NET Core keeps SaveTokens' tokens in the properties (not in claims), and the cookie
        // handler serialises the properties into the cookie: the claim assertions above cannot see them.
        var identity = new ClaimsIdentity([new Claim("sub", "admin-id")], "test");
        var context = NewContext(identity, new OpenIdConnectMessage
        {
            AccessToken = "the-access-token",
            RefreshToken = "the-refresh-token",
            IdToken = "the-id-token",
            ExpiresIn = "3600",
        });

        await TokenSessionEvents.OnTokenValidated(context);

        var properties = context.Properties!;
        properties.GetTokens().Should().BeEmpty(
            "the tokens live in the server-side store; none may ride in the properties the cookie is built from");
        properties.Items.Values.Should().NotContain(value =>
            value != null && (value.Contains("the-access-token") || value.Contains("the-refresh-token") || value.Contains("the-id-token")));
    }

    [Fact]
    public async Task OnTokenValidated_MapsRoleAndGroupClaimsToRoleClaims()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim("sub", "admin-id"),
            new Claim("groups", "Admin"),
            new Claim("role", "Editor")
        ], "test");

        await TokenSessionEvents.OnTokenValidated(NewContext(identity));

        identity.FindAll(ClaimTypes.Role).Select(c => c.Value).Should().BeEquivalentTo("Admin", "Editor");
    }

    [Fact]
    public async Task OnTokenValidated_ConfiguredRoleGroup_MapsToRoleClaim()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Oidc:RoleGroups:Admin"] = "Blog Admins",
            ["Oidc:RoleGroups:Editor"] = "Blog Editors"
        }).Build();
        var identity = new ClaimsIdentity(
        [
            new Claim("sub", "admin-id"),
            new Claim("groups", "Blog Admins")
        ], "test");

        await TokenSessionEvents.OnTokenValidated(NewContext(identity, configuration: configuration));

        // The configured group name grants the role, not the raw group name.
        identity.FindAll(ClaimTypes.Role).Select(c => c.Value).Should().Equal("Admin");
    }

    [Fact]
    public async Task OnTokenValidated_StoresTokensUnderSessionId()
    {
        var now = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        var store = new InMemoryTokenStore();
        var identity = new ClaimsIdentity([new Claim("sub", "admin-id")], "test");
        var context = NewContext(identity, new OpenIdConnectMessage
        {
            AccessToken = "at-1",
            RefreshToken = "rt-1",
            ExpiresIn = "3600"
        }, store, now);

        await TokenSessionEvents.OnTokenValidated(context);

        var sid = identity.FindFirst("blog_sid")!.Value;
        (await store.GetAsync(sid)).Should().Be(new TokenEntry("at-1", "rt-1", now.AddSeconds(3600), Sub: "admin-id", SessionExpiresAt: now.AddDays(30)));
    }

    [Fact]
    public async Task OnTokenValidated_StoresIdpSidAndSubInTheTokenStore()
    {
        var now = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        var store = new InMemoryTokenStore();
        var identity = new ClaimsIdentity(
        [
            new Claim("sub", "admin-id"),
            new Claim("sid", "idp-sid-1")
        ], "test");
        var context = NewContext(identity, new OpenIdConnectMessage
        {
            AccessToken = "at-1",
            ExpiresIn = "3600"
        }, store, now);

        await TokenSessionEvents.OnTokenValidated(context);

        var blogSid = identity.FindFirst("blog_sid")!.Value;
        (await store.GetAsync(blogSid))!.Sid.Should().Be("idp-sid-1",
            "a back-channel logout token identifies the session by the IdP's sid claim");
        (await store.GetAsync(blogSid))!.Sub.Should().Be("admin-id",
            "a back-channel logout token without sid identifies the user by sub");
    }

    [Fact]
    public async Task OnTokenValidated_StoresIdTokenInTheTokenStore()
    {
        var now = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        var store = new InMemoryTokenStore();
        var identity = new ClaimsIdentity([new Claim("sub", "admin-id")], "test");
        var context = NewContext(identity, new OpenIdConnectMessage
        {
            AccessToken = "at-1",
            RefreshToken = "rt-1",
            IdToken = "idt-1",
            ExpiresIn = "3600"
        }, store, now);

        await TokenSessionEvents.OnTokenValidated(context);

        var sid = identity.FindFirst("blog_sid")!.Value;
        (await store.GetAsync(sid))!.IdToken.Should().Be("idt-1",
            "the end-session request needs the ID token as id_token_hint, so the store must keep it");
    }

    [Fact]
    public async Task OnTokenValidated_DoesNotAddIdTokenClaim()
    {
        var identity = new ClaimsIdentity([new Claim("sub", "admin-id")], "test");
        var context = NewContext(identity, new OpenIdConnectMessage
        {
            AccessToken = "the-access-token",
            IdToken = "the-id-token"
        });

        await TokenSessionEvents.OnTokenValidated(context);

        identity.Claims.Should().NotContain(claim => claim.Value == "the-id-token",
            "the ID token lives in the server-side store only, never in the cookie");
    }

    [Fact]
    public async Task OnTokenValidated_MissingExpiresInWithJwtAccessToken_StoresJwtExpAsExpiry()
    {
        var now = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        var exp = now.AddSeconds(7200);
        var jwt = TestJwt.Create(exp);
        var store = new InMemoryTokenStore();
        var identity = new ClaimsIdentity([new Claim("sub", "admin-id")], "test");
        var context = NewContext(identity, new OpenIdConnectMessage
        {
            AccessToken = jwt,
            RefreshToken = "rt-1"
        }, store, now);

        await TokenSessionEvents.OnTokenValidated(context);

        var sid = identity.FindFirst("blog_sid")!.Value;
        (await store.GetAsync(sid)).Should().Be(new TokenEntry(jwt, "rt-1", exp, Sub: "admin-id", SessionExpiresAt: now.AddDays(30)),
            "without expires_in the access token's own exp claim says when it expires");
    }

    [Fact]
    public async Task OnTokenValidated_MissingExpiresInWithOpaqueToken_Stores300SecondExpiry()
    {
        var now = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        var store = new InMemoryTokenStore();
        var identity = new ClaimsIdentity([new Claim("sub", "admin-id")], "test");
        var context = NewContext(identity, new OpenIdConnectMessage
        {
            AccessToken = "at-1",
            RefreshToken = "rt-1"
        }, store, now);

        await TokenSessionEvents.OnTokenValidated(context);

        var sid = identity.FindFirst("blog_sid")!.Value;
        (await store.GetAsync(sid)).Should().Be(new TokenEntry("at-1", "rt-1", now.AddSeconds(300), Sub: "admin-id", SessionExpiresAt: now.AddDays(30)),
            "without expires_in and an opaque access token, default to 300 seconds");
    }

    [Fact]
    public async Task OnValidatePrincipal_SessionInStore_KeepsPrincipal()
    {
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", new TokenEntry("at-1", "rt-1", Now.AddSeconds(3600)));
        var authService = new FakeAuthenticationService();
        var context = NewCookieContext(UserWithSid("s1"), store, authService);

        await TokenSessionEvents.OnValidatePrincipal(context);

        context.Principal.Should().NotBeNull("a session with a store entry is still logged in");
        authService.SignOutCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task OnValidatePrincipal_NoStoreEntry_RejectsPrincipalAndSignsOut()
    {
        var authService = new FakeAuthenticationService();
        var context = NewCookieContext(UserWithSid("s1"), new InMemoryTokenStore(), authService);

        await TokenSessionEvents.OnValidatePrincipal(context);

        context.Principal.Should().BeNull("the session is gone server-side, e.g. after a pod restart");
        authService.SignOutCalls.Should().ContainSingle().Which.Should().Be("Cookies");
    }

    [Fact]
    public async Task OnValidatePrincipal_NoBlogSidClaim_RejectsPrincipalAndSignsOut()
    {
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", new TokenEntry("at-1", "rt-1", Now.AddSeconds(3600)));
        var authService = new FakeAuthenticationService();
        var context = NewCookieContext(UserWithSid(null), store, authService);

        await TokenSessionEvents.OnValidatePrincipal(context);

        context.Principal.Should().BeNull();
        authService.SignOutCalls.Should().ContainSingle().Which.Should().Be("Cookies");
    }

    [Fact]
    public async Task RemoveSession_WithBlogSid_RemovesTheStoreEntry()
    {
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", new TokenEntry("at-1", "rt-1", Now.AddSeconds(3600)));

        await TokenSessionEvents.RemoveSessionAsync(UserWithSid("s1"), store);

        (await store.GetAsync("s1")).Should().BeNull();
    }

    [Fact]
    public async Task RemoveSession_WithoutBlogSid_LeavesStoreUntouched()
    {
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", new TokenEntry("at-1", "rt-1", Now.AddSeconds(3600)));

        await TokenSessionEvents.RemoveSessionAsync(UserWithSid(null), store);

        (await store.GetAsync("s1")).Should().NotBeNull();
    }

    [Fact]
    public async Task LogoutAsync_WithStoredIdToken_SignsOutOfCookieAndIdP()
    {
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", new TokenEntry("at-1", "rt-1", Now.AddSeconds(3600), "idt-1"));
        var authService = new FakeAuthenticationService();
        var httpContext = NewHttpContext(UserWithSid("s1"), store, authService);

        await TokenSessionEvents.LogoutAsync(httpContext, store);

        (await store.GetAsync("s1")).Should().BeNull("the session's tokens go when the user signs out");
        authService.SignOutCalls.Should().Equal(
            ["Cookies", "OpenIdConnect"],
            "logout ends both the cookie session and the IdP session");
    }

    [Fact]
    public async Task LogoutAsync_WithStoredIdToken_SendsItAsIdTokenHintOnTheEndSessionRequest()
    {
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", new TokenEntry("at-1", "rt-1", Now.AddSeconds(3600), "idt-1"));
        var httpContext = NewHttpContext(UserWithSid("s1"), store, new FakeAuthenticationService());

        await TokenSessionEvents.LogoutAsync(httpContext, store);

        var redirect = NewRedirectContext(httpContext);
        await TokenSessionEvents.OnRedirectToIdentityProviderForSignOut(redirect);

        redirect.ProtocolMessage.IdTokenHint.Should().Be("idt-1",
            "the OIDC provider rejects the end-session redirect with 400 unless id_token_hint is set");
    }

    [Theory]
    // The session expired or the pod restarted: no store entry at all.
    [InlineData(false)]
    // The session is in the store but has no ID token.
    [InlineData(true)]
    public async Task LogoutAsync_NoStoredIdToken_SignsOutLocallyAndRedirectsHome(bool entryWithoutIdToken)
    {
        var store = new InMemoryTokenStore();
        if (entryWithoutIdToken)
        {
            await store.SetAsync("s1", new TokenEntry("at-1", "rt-1", Now.AddSeconds(3600)));
        }
        var authService = new FakeAuthenticationService();
        var httpContext = NewHttpContext(UserWithSid("s1"), store, authService);

        await TokenSessionEvents.LogoutAsync(httpContext, store);

        authService.SignOutCalls.Should().Equal(
            ["Cookies"],
            "the IdP would reject an end-session request without a stored ID token, so it must be skipped");
        (await store.GetAsync("s1")).Should().BeNull();
        httpContext.Response.StatusCode.Should().Be(302);
        httpContext.Response.Headers.Location.ToString().Should().Be("/",
            "a local sign-out still ends at the blog's home page");
    }

    private static CookieValidatePrincipalContext NewCookieContext(
        ClaimsPrincipal user, ITokenStore store, FakeAuthenticationService authService)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(store);
        services.AddSingleton<IAuthenticationService>(authService);
        return new CookieValidatePrincipalContext(
            new DefaultHttpContext { RequestServices = services.BuildServiceProvider() },
            new AuthenticationScheme("Cookies", null, typeof(CookieAuthenticationHandler)),
            new CookieAuthenticationOptions(),
            new AuthenticationTicket(user, new AuthenticationProperties(), "Cookies"));
    }

    private static DefaultHttpContext NewHttpContext(
        ClaimsPrincipal user, ITokenStore store, FakeAuthenticationService authService)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton<IAuthenticationService>(authService);
        var httpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        httpContext.User = user;
        return httpContext;
    }

    private static RedirectContext NewRedirectContext(HttpContext httpContext) =>
        new(httpContext,
            new AuthenticationScheme("OpenIdConnect", null, typeof(OpenIdConnectHandler)),
            new OpenIdConnectOptions(),
            new AuthenticationProperties())
        {
            ProtocolMessage = new OpenIdConnectMessage()
        };

    private static ClaimsPrincipal UserWithSid(string? sid)
    {
        var claims = new List<Claim> { new("sub", "admin-id") };
        if (sid != null)
        {
            claims.Add(new Claim("blog_sid", sid));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private sealed class FakeAuthenticationService : IAuthenticationService
    {
        public List<string> SignOutCalls = [];

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.NoResult());

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            Task.CompletedTask;

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            Task.CompletedTask;

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) =>
            Task.CompletedTask;

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            SignOutCalls.Add(scheme ?? "");
            return Task.CompletedTask;
        }
    }

    private static TokenValidatedContext NewContext(
        ClaimsIdentity identity,
        OpenIdConnectMessage? tokenResponse = null,
        ITokenStore? store = null,
        DateTimeOffset? now = null,
        IConfiguration? configuration = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITokenStore>(store ?? new InMemoryTokenStore());
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(now ?? DateTimeOffset.UnixEpoch));
        services.AddSingleton(new AuthSettings(TimeSpan.FromDays(30)));
        services.Configure<RoleGroupSettings>(
            (configuration ?? new ConfigurationBuilder().Build()).GetSection(RoleGroupSettings.SectionName));

        var context = new TokenValidatedContext(
            new DefaultHttpContext { RequestServices = services.BuildServiceProvider() },
            new AuthenticationScheme("OpenIdConnect", null, typeof(OpenIdConnectHandler)),
            new OpenIdConnectOptions(),
            new ClaimsPrincipal(identity),
            new AuthenticationProperties());
        context.TokenEndpointResponse = tokenResponse;
        return context;
    }
}
