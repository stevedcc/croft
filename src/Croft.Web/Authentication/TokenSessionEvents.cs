using System.Security.Claims;
using System.Security.Cryptography;
using Croft.Shared;
using Croft.Web.Options;
using Croft.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

namespace Croft.Web.Authentication;

public static class TokenSessionEvents
{
    public const string SessionIdClaim = "blog_sid";

    /// <summary>
    /// HttpContext.Items key holding the ID token of the sign-out in flight, from the store
    /// entry's removal until the end-session redirect is built.
    /// </summary>
    public const string SignOutIdTokenItemsKey = "blog_signout_id_token";

    public static async Task OnTokenValidated(TokenValidatedContext context)
    {
        if (context.Principal?.Identity is ClaimsIdentity identity)
        {
            var settings = context.HttpContext.RequestServices
                .GetRequiredService<IOptions<RoleGroupSettings>>().Value;
            RoleGroupMapper.AddRoleClaims(identity, settings);

            var sessionId = NewSessionId();
            identity.AddClaim(new Claim(SessionIdClaim, sessionId));

            await SaveTokensAsync(context, sessionId, identity);
        }
    }

    private static async Task SaveTokensAsync(TokenValidatedContext context, string sessionId, ClaimsIdentity identity)
    {
        var tokens = context.TokenEndpointResponse;
        if (string.IsNullOrEmpty(tokens?.AccessToken))
        {
            return;
        }

        var services = context.HttpContext.RequestServices;
        var expiresAt = TokenExpiry.ResolveExpiresAt(
            services.GetRequiredService<TimeProvider>().GetUtcNow(),
            tokens!.ExpiresIn,
            tokens.AccessToken!);
        var sessionExpiresAt = TokenExpiry.ResolveSessionExpiresAt(
            services.GetRequiredService<TimeProvider>().GetUtcNow(),
            tokens.GetParameter("refresh_expires_in"),
            services.GetRequiredService<AuthSettings>().SessionLifetime);
        // The IdP's sid and sub go into the entry so a back-channel logout token can find this
        // session (by sid) or every session of the user (by sub).
        await services.GetRequiredService<ITokenStore>().SetAsync(sessionId, new TokenEntry(
            tokens!.AccessToken!,
            tokens.RefreshToken,
            expiresAt,
            tokens.IdToken,
            Sid: identity.FindFirst("sid")?.Value,
            Sub: identity.FindFirst("sub")?.Value,
            SessionExpiresAt: sessionExpiresAt));
    }

    private static string NewSessionId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// Rejects cookie principals whose server-side session is gone (e.g. after a pod
    /// restart): the user is signed out instead of staying "logged in" with no token.
    /// </summary>
    public static async Task OnValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var store = context.HttpContext.RequestServices.GetRequiredService<ITokenStore>();
        var sessionId = context.Principal?.FindFirst(SessionIdClaim)?.Value;
        if (string.IsNullOrEmpty(sessionId) || await store.GetAsync(sessionId, context.HttpContext.RequestAborted) is null)
        {
            SessionRemoval.LogEntryMissing(
                context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(TokenSessionEvents).FullName!),
                sessionId);
            // WORKAROUND(W-043, Blazor): cookie validation runs only on HTTP requests, never inside a circuit; the API client's 401 handling covers that gap
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            // END WORKAROUND(W-043)
        }
    }

    /// <summary>Removes the user's token-store entry (used at sign-out).</summary>
    public static Task RemoveSessionAsync(ClaimsPrincipal user, ITokenStore store)
    {
        var sessionId = user.FindFirst(SessionIdClaim)?.Value;
        return string.IsNullOrEmpty(sessionId) ? Task.CompletedTask : store.RemoveAsync(sessionId);
    }

    /// <summary>
    /// The session's stored ID token, if any. Read before the store entry is removed at
    /// sign-out: the end-session request needs it as id_token_hint.
    /// </summary>
    private static async Task<string?> FindIdTokenAsync(ClaimsPrincipal user, ITokenStore store)
    {
        var sessionId = user.FindFirst(SessionIdClaim)?.Value;
        return string.IsNullOrEmpty(sessionId) ? null : (await store.GetAsync(sessionId))?.IdToken;
    }

    // WORKAROUND(W-046, OIDC): Authentik rejects end-session without id_token_hint, and SaveTokens is off, so the ID token comes from the server-side store
    /// <summary>
    /// Signs the user out of the cookie and the IdP. The ID token is captured first (the
    /// end-session request carries it as id_token_hint via
    /// <see cref="OnRedirectToIdentityProviderForSignOut"/>), then the session's store entry
    /// is removed. Without a stored ID token (expired session, pod restart) the OIDC provider
    /// rejects the end-session request (400 without id_token_hint), so the
    /// sign-out stays local and lands on "/".
    /// </summary>
    public static async Task LogoutAsync(HttpContext context, ITokenStore store)
    {
        var idToken = await FindIdTokenAsync(context.User, store);
        await RemoveSessionAsync(context.User, store);
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        if (idToken is null)
        {
            context.Response.Redirect("/");
            return;
        }

        context.Items[SignOutIdTokenItemsKey] = idToken;
        await context.SignOutAsync(OpenIdConnectDefaults.AuthenticationScheme, new AuthenticationProperties
        {
            RedirectUri = "/"
        });
    }
    // END WORKAROUND(W-046)

    // WORKAROUND(W-046, OIDC): puts the stored ID token on the end-session request as id_token_hint
    /// <summary>
    /// Puts the ID token captured by <see cref="LogoutAsync"/> on the end-session request as
    /// id_token_hint. Authentik, for example, rejects a post-logout redirect (400) without it.
    /// </summary>
    public static Task OnRedirectToIdentityProviderForSignOut(RedirectContext context)
    {
        if (context.HttpContext.Items[SignOutIdTokenItemsKey] is string idToken)
        {
            context.ProtocolMessage.IdTokenHint = idToken;
        }

        return Task.CompletedTask;
    }
    // END WORKAROUND(W-046)
}
