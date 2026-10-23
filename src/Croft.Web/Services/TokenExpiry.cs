using System.Globalization;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Croft.Web.Services;

/// <summary>
/// Turns a token response's <c>expires_in</c> into an absolute expiry. Shared by login
/// (<see cref="Authentication.TokenSessionEvents.SaveTokensAsync"/>) and refresh
/// (<see cref="UserAccessTokenService"/>), so the two never disagree.
/// </summary>
public static class TokenExpiry
{
    // Bounds of DateTimeOffset.FromUnixTimeSeconds.
    private const long MinUnixSeconds = -62_135_596_800;
    private const long MaxUnixSeconds = 253_402_300_799;

    private const double DefaultLifetimeSeconds = 300;

    /// <summary>
    /// A missing, zero, negative or unparseable <paramref name="expiresInSeconds"/> falls back
    /// to the access token's own <c>exp</c> claim when the token is a readable JWT, and failing
    /// that to a 300-second default: an unknown lifetime must not make the token look stale at once.
    /// </summary>
    public static DateTimeOffset ResolveExpiresAt(DateTimeOffset now, string? expiresInSeconds, string accessToken)
    {
        if (TryGetSeconds(expiresInSeconds, out var seconds) && seconds > 0)
        {
            return now.AddSeconds(seconds);
        }

        return TryGetExp(accessToken, out var exp) ? exp : now.AddSeconds(DefaultLifetimeSeconds);
    }

    /// <summary>
    /// When the session as a whole ends: the token response's <c>refresh_expires_in</c> from
    /// <paramref name="now"/>, or <paramref name="fallbackLifetime"/> (<c>Auth:SessionLifetime</c>)
    /// when the response has none or an unusable one (zero, negative, not a number).
    /// </summary>
    public static DateTimeOffset ResolveSessionExpiresAt(
        DateTimeOffset now, string? refreshExpiresInSeconds, TimeSpan fallbackLifetime) =>
        TryGetSeconds(refreshExpiresInSeconds, out var seconds) && seconds > 0
            ? now.AddSeconds(seconds)
            : now.Add(fallbackLifetime);

    /// <summary>
    /// Reads the <c>exp</c> claim from an unvalidated JWT. A past <c>exp</c> is still
    /// usable: the token says itself when it expired.
    /// </summary>
    private static bool TryGetExp(string accessToken, out DateTimeOffset exp)
    {
        exp = default;
        if (string.IsNullOrEmpty(accessToken))
        {
            return false;
        }

        JsonWebToken token;
        try
        {
            token = new JsonWebTokenHandler().ReadJsonWebToken(accessToken);
        }
        catch (Exception ex) when (ex is SecurityTokenException or JsonException or ArgumentException)
        {
            // Not a readable JWT: treat the access token as opaque.
            return false;
        }

        foreach (var claim in token.Claims)
        {
            if (claim.Type == JwtRegisteredClaimNames.Exp
                && TryGetSeconds(claim.Value, out var seconds)
                && seconds is >= MinUnixSeconds and <= MaxUnixSeconds)
            {
                exp = DateTimeOffset.FromUnixTimeSeconds(seconds);
                return true;
            }
        }

        return false;
    }

    private static bool TryGetSeconds(string? value, out long seconds)
    {
        seconds = 0;
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var exact))
        {
            seconds = exact;
            return true;
        }
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fractional)
            && fractional >= 0 && fractional < long.MaxValue)
        {
            seconds = (long)Math.Floor(fractional);
            return true;
        }
        return false;
    }
}
