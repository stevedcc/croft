using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Croft.Web.Authentication;

/// <summary>
/// Validates an OIDC Back-Channel Logout 1.0 logout token (spec section 2.6) against the IdP's
/// signing keys and returns its identifying claims, or null when any rule rejects it. The token
/// is a credential and must never be logged by callers. The issuer is the one in the discovered
/// OIDC metadata.
/// </summary>
public sealed class BackchannelLogoutTokenValidator(
    string clientId,
    IConfigurationManager<OpenIdConnectConfiguration> configurationManager,
    TimeProvider time)
{
    public const string LogoutEvent = "http://schemas.openid.net/event/backchannel-logout";

    // The spec requires rejecting a logout token whose iat is "too far in the past" without
    // naming a value: five minutes absorbs delivery delays while keeping the replay window
    // small, and two minutes of forward skew absorbs ordinary clock drift between the hosts.
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);

    private static readonly JsonWebTokenHandler Handler = new();

    public async Task<ClaimsPrincipal?> ValidateAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var configuration = await configurationManager.GetConfigurationAsync(ct);
        var result = await Handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidateIssuer = true,
            // The issuer the IdP announces in its metadata, not the configured authority string:
            // the two differ by a trailing slash or the host's casing, and every logout token
            // would be rejected with an error that reads like an attack.
            ValidIssuer = configuration.Issuer,
            ValidateAudience = true,
            ValidAudience = clientId,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = configuration.SigningKeys,
            RequireSignedTokens = true,
            // Logout tokens carry no exp/nbf. The spec's replay defence is jti plus a replay
            // cache, which is not implemented here; the iat freshness rule below bounds the
            // replay window to MaxAge. jti presence itself is enforced in ValidateClaims.
            RequireExpirationTime = false,
            ValidateLifetime = false
        });
        if (!result.IsValid)
        {
            return null;
        }

        return ParsePayload(token) is { } payload ? ValidateClaims(payload) : null;
    }

    /// <summary>
    /// The payload JSON of the signature-validated token. It is read from the token string
    /// instead of the mapped claims because the rules need exact JSON shapes: the events member
    /// is an object, nonce must be entirely absent, and iat is a number.
    /// </summary>
    private static JsonElement? ParsePayload(string token)
    {
        try
        {
            var segments = token.Split('.');
            if (segments.Length != 3)
            {
                return null;
            }

            var json = Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(segments[1]));
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    private ClaimsPrincipal? ValidateClaims(JsonElement payload)
    {
        if (payload.TryGetProperty("nonce", out _))
        {
            return null;
        }

        // jti is REQUIRED (spec section 2.6): it is the identifier a replay cache would track.
        if (ReadString(payload, "jti") is not { Length: > 0 })
        {
            return null;
        }

        var isLogoutEvent =
            payload.TryGetProperty("events", out var events) &&
            events.ValueKind is JsonValueKind.Object &&
            events.TryGetProperty(LogoutEvent, out var logoutEvent) &&
            logoutEvent.ValueKind is JsonValueKind.Object;
        if (!isLogoutEvent)
        {
            return null;
        }

        if (!payload.TryGetProperty("iat", out var iatClaim) || !iatClaim.TryGetInt64(out var iatSeconds))
        {
            return null;
        }

        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(iatSeconds);
        var now = time.GetUtcNow();
        if (issuedAt < now - MaxAge || issuedAt > now + ClockSkew)
        {
            return null;
        }

        var sid = ReadString(payload, "sid");
        var sub = ReadString(payload, "sub");
        if (string.IsNullOrEmpty(sid) && string.IsNullOrEmpty(sub))
        {
            return null;
        }

        var identity = new ClaimsIdentity("BackchannelLogout");
        if (!string.IsNullOrEmpty(sid))
        {
            identity.AddClaim(new Claim("sid", sid));
        }

        if (!string.IsNullOrEmpty(sub))
        {
            identity.AddClaim(new Claim("sub", sub));
        }

        return new ClaimsPrincipal(identity);
    }

    private static string? ReadString(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;
}
