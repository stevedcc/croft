namespace Croft.Web.Services;

/// <summary>
/// A login session's tokens as the IdP returned them, kept server-side only. The ID token is
/// kept solely to send as id_token_hint on the end-session request at sign-out. Sid and Sub
/// mirror the IdP's claims, so a back-channel logout token can find this session by sid — or,
/// carrying only a sub, every session of that user. <c>ExpiresAt</c> is when the access token
/// must be refreshed; <c>SessionExpiresAt</c> is when the whole session ends (the refresh
/// token's lifetime), after which the purge may delete it.
/// </summary>
public sealed record TokenEntry(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset ExpiresAt,
    string? IdToken = null,
    string? Sid = null,
    string? Sub = null,
    DateTimeOffset? SessionExpiresAt = null);
