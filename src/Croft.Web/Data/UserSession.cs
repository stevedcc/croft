namespace Croft.Web.Data;

/// <summary>
/// One login session's token entry in the Web database (the shared token store's row): the
/// session id keys the row, Sid and Sub mirror the IdP's claims for back-channel logout, and the
/// tokens are stored protected with Data Protection by <see cref="Croft.Web.Services.PostgresTokenStore"/>, never in clear text.
/// </summary>
public class UserSession
{
    public required string SessionId { get; set; }
    public string? Sid { get; set; }
    public string? Sub { get; set; }
    public string AccessToken { get; set; } = "";
    public string? RefreshToken { get; set; }
    public string? IdToken { get; set; }
    /// <summary>When the access token expires and must be refreshed.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// When the session as a whole ends (the refresh token's lifetime): the purge deletes rows
    /// past this moment. Null only for a row written by a release that did not know the column.
    /// </summary>
    public DateTimeOffset? SessionExpiresAt { get; set; }
}
