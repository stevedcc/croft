using Croft.Web.Authentication;

namespace Croft.Web.Services;

/// <summary>
/// Ends blog sessions on an OIDC back-channel logout: once the logout token is validated,
/// every session of its sid loses its token-store entry — falling back to every session of its
/// sub when the sid matches nothing (e.g. the ID token at login carried no sid) — and the cookie
/// handler rejects the login cookie on the next request.
/// </summary>
public sealed class BackchannelLogoutService(
    ITokenStore store,
    BackchannelLogoutTokenValidator validator,
    ILogger<BackchannelLogoutService> logger)
{
    /// <summary>True when the token was valid and the matching sessions were removed.</summary>
    public async Task<bool> LogoutAsync(string? logoutToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(logoutToken))
        {
            return false;
        }

        if (await validator.ValidateAsync(logoutToken, ct) is not { } principal)
        {
            return false;
        }

        var sid = principal.FindFirst("sid")?.Value;
        var sub = principal.FindFirst("sub")?.Value;
        if (!string.IsNullOrEmpty(sid))
        {
            var removed = await store.RemoveBySidAsync(sid, ct);
            if (removed == 0 && !string.IsNullOrEmpty(sub))
            {
                removed = await store.RemoveBySubAsync(sub, ct);
            }

            SessionRemoval.LogBackChannelLogout(logger, removed);
            return true;
        }

        if (!string.IsNullOrEmpty(sub))
        {
            SessionRemoval.LogBackChannelLogout(logger, await store.RemoveBySubAsync(sub, ct));
            return true;
        }

        return false;
    }
}
