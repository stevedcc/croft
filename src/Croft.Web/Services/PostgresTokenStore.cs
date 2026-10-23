using System.Security.Cryptography;
using Croft.Web.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Croft.Web.Services;

/// <summary>
/// The token store shared by every Web instance (ConnectionStrings:WebDb): sessions are rows in
/// the Web database, so any instance can read, refresh and end any signed-in user's session. One
/// context per operation from the factory — the store is a singleton and must not hold a scoped
/// context.
/// <para>
/// The access, refresh and ID tokens are stored protected with Data Protection (purpose
/// <see cref="Purpose"/>), never in clear text: a copy of the database yields no usable token unless
/// it also yields the key ring (the ring is encrypted with a certificate when one is configured,
/// <see cref="KeyRingEncryption"/>). A row whose tokens cannot be unprotected (a row written in
/// clear text by an earlier release, a key that was rotated away) reads as a missing session: the
/// user signs in again. A row past its session expiry (Auth:SessionLifetime, or the refresh token's
/// lifetime) reads as missing too, before the purge job has deleted it, so the cookie is rejected and
/// nothing is refreshed for it.
/// </para>
/// </summary>
public sealed class PostgresTokenStore(
    IDbContextFactory<CroftWebDbContext> factory, IDataProtectionProvider dataProtection, TimeProvider time)
    : ITokenStore, ISessionLock
{
    /// <summary>The Data Protection purpose of the stored tokens; the suffix is the storage format's version.</summary>
    public const string Purpose = "Croft.Web.TokenStore.v1";

    private readonly IDataProtector _protector = dataProtection.CreateProtector(Purpose);

    public async Task<TokenEntry?> GetAsync(string sessionId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.UserSessions
            .AsNoTracking()
            .Where(s => s.SessionId == sessionId)
            .FirstOrDefaultAsync(ct);
        return row is null ? null : ToEntry(row);
    }

    public async Task SetAsync(string sessionId, TokenEntry entry, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await UpsertAsync(db, sessionId, entry, ct);
    }

    public async Task RemoveAsync(string sessionId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.UserSessions.Where(s => s.SessionId == sessionId).ExecuteDeleteAsync(ct);
    }

    public async Task<int> RemoveBySidAsync(string sid, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.UserSessions.Where(s => s.Sid == sid).ExecuteDeleteAsync(ct);
    }

    public async Task<int> RemoveBySubAsync(string sub, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.UserSessions.Where(s => s.Sub == sub).ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// The refresh path's serialisation: the transaction's <c>SELECT ... FOR UPDATE</c> locks the
    /// session's row and returns it as re-read under that lock (the lock acquisition sees the
    /// latest committed row — including the racing winner's freshly stored tokens). The work's
    /// writes go to the same transaction and commit before the lock is released, so the next
    /// refresher in line re-reads the winner's tokens instead of spending the rotated refresh
    /// token again. A missing row locks nothing and reads as null.
    /// </summary>
    public async Task<T> RunLockedAsync<T>(
        string sessionId, Func<LockedSession, CancellationToken, Task<T>> work, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var row = await db.UserSessions
            .FromSqlInterpolated($"""
                SELECT * FROM "UserSessions" WHERE "SessionId" = {sessionId} FOR UPDATE
                """)
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);

        var entry = row is null ? null : ToEntry(row);

        var result = await work(new LockedSession(entry, async (updated, token) =>
        {
            if (updated is null)
            {
                await db.UserSessions.Where(s => s.SessionId == sessionId).ExecuteDeleteAsync(token);
            }
            else
            {
                await UpsertAsync(db, sessionId, updated, token);
            }
        }), ct);

        await transaction.CommitAsync(ct);
        return result;
    }

    private TokenEntry? ToEntry(UserSession row)
    {
        if (row.SessionExpiresAt < time.GetUtcNow())
        {
            return null;
        }

        try
        {
            return new TokenEntry(
                _protector.Unprotect(row.AccessToken),
                row.RefreshToken is null ? null : _protector.Unprotect(row.RefreshToken),
                row.ExpiresAt,
                row.IdToken is null ? null : _protector.Unprotect(row.IdToken),
                row.Sid,
                row.Sub,
                row.SessionExpiresAt);
        }
        catch (CryptographicException)
        {
            // Not protected by this key ring (clear text from before the upgrade, a rotated-away
            // key): no session. Nothing of the value goes to the log.
            return null;
        }
    }

    // WORKAROUND(W-032, EF Core): EF has no upsert; the SQL hard-codes the default column names
    /// <summary>
    /// Last writer wins. The column names are quoted because the table's names are EF's default
    /// (property names); the insert-or-update is one statement so two writers of one session
    /// cannot collide on the primary key.
    /// </summary>
    private Task UpsertAsync(CroftWebDbContext db, string sessionId, TokenEntry entry, CancellationToken ct)
    {
        var accessToken = _protector.Protect(entry.AccessToken);
        var refreshToken = entry.RefreshToken is null ? null : _protector.Protect(entry.RefreshToken);
        var idToken = entry.IdToken is null ? null : _protector.Protect(entry.IdToken);
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "UserSessions" ("SessionId", "Sid", "Sub", "AccessToken", "RefreshToken", "IdToken", "ExpiresAt", "SessionExpiresAt")
            VALUES ({sessionId}, {entry.Sid}, {entry.Sub}, {accessToken}, {refreshToken}, {idToken}, {entry.ExpiresAt}, {entry.SessionExpiresAt})
            ON CONFLICT ("SessionId") DO UPDATE SET
                "Sid" = EXCLUDED."Sid",
                "Sub" = EXCLUDED."Sub",
                "AccessToken" = EXCLUDED."AccessToken",
                "RefreshToken" = EXCLUDED."RefreshToken",
                "IdToken" = EXCLUDED."IdToken",
                "ExpiresAt" = EXCLUDED."ExpiresAt",
                "SessionExpiresAt" = EXCLUDED."SessionExpiresAt"
            """, ct);
    }
    // END WORKAROUND(W-032)
}
