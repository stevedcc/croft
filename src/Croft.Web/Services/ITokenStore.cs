namespace Croft.Web.Services;

/// <summary>
/// A login session's token entry storage. The key is the session id the login flow puts in the
/// <c>blog_sid</c> claim. Singleton-scoped: with ConnectionStrings:WebDb the implementation is
/// <see cref="PostgresTokenStore"/> (shared by every Web instance), otherwise
/// <see cref="InMemoryTokenStore"/> (the single-instance default).
/// </summary>
public interface ITokenStore
{
    Task<TokenEntry?> GetAsync(string sessionId, CancellationToken ct = default);
    Task SetAsync(string sessionId, TokenEntry entry, CancellationToken ct = default);
    Task RemoveAsync(string sessionId, CancellationToken ct = default);

    /// <summary>Removes every session whose entry carries this IdP sid; returns how many.</summary>
    Task<int> RemoveBySidAsync(string sid, CancellationToken ct = default);

    /// <summary>Removes every session of this user (IdP sub); returns how many.</summary>
    Task<int> RemoveBySubAsync(string sub, CancellationToken ct = default);
}
