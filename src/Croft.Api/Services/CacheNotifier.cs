using Croft.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Croft.Api.Services;

/// <summary>
/// Invalidates the public read cache for a content write, atomically with the write (brief 13c
/// item 3). The NOTIFY is sent inside the write's transaction, so Postgres delivers it to every
/// listening instance when the write commits and only then; the writer's own instance is cleared
/// right after the commit, and clearing twice (once more when its own listener hears the
/// notification) is harmless.
/// </summary>
public interface ICacheNotifier
{
    /// <summary>
    /// Saves the context's pending changes and queues the NOTIFY for <paramref name="changed"/> in one
    /// transaction, commits it, then clears this instance's cache. If the NOTIFY fails the write is
    /// rolled back. Nothing after the commit is cancelled by <paramref name="ct"/>: a write that
    /// committed always clears the local cache, even when the request is already gone.
    /// </summary>
    Task SaveAndNotifyAsync(BlogDbContext db, CacheGroup changed, CancellationToken ct = default);

    /// <summary>
    /// For work that runs its own transaction (the background jobs): queues the NOTIFY on the
    /// context's current transaction, so it is delivered when that transaction commits. Call
    /// <see cref="InvalidateLocalAsync"/> after the commit.
    /// </summary>
    Task NotifyAsync(DbContext db, CacheGroup changed, CancellationToken ct = default);

    /// <summary>Clears this instance's entries for <paramref name="changed"/>; never cancelled, call it only after the commit.</summary>
    Task InvalidateLocalAsync(CacheGroup changed);
}

public sealed class CacheNotifier(IPublicReadCache cache) : ICacheNotifier
{
    /// <summary>The channel every replica listens on.</summary>
    public const string Channel = "croft_cache";

    /// <summary>
    /// The payload of one notification: the changed groups' lower-case names, comma separated
    /// (<c>categories,tags,posts,pages</c> for <see cref="CacheGroup.All"/>). A single group is the bare
    /// name, as the releases before 13c sent it; the listener reads both forms.
    /// </summary>
    public static string Payload(CacheGroup changed) =>
        string.Join(',', CacheGroups.Singles.Where(group => changed.HasFlag(group)).Select(group => group.ToString().ToLowerInvariant()));

    /// <summary>Reads a notification payload; false when any name in it is unknown.</summary>
    public static bool TryParsePayload(string payload, out CacheGroup groups)
    {
        groups = 0;
        foreach (var name in payload.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<CacheGroup>(name, ignoreCase: true, out var group) || group == CacheGroup.All || !CacheGroups.Singles.Contains(group))
                return false;
            groups |= group;
        }

        return groups != 0;
    }

    public async Task SaveAndNotifyAsync(BlogDbContext db, CacheGroup changed, CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is not null)
        {
            // The caller owns the transaction (and its commit): the write and the NOTIFY join it.
            await db.SaveChangesAsync(ct);
            await NotifyAsync(db, changed, ct);
            return;
        }

        // The transaction lives inside the execution strategy so a retrying strategy can rerun it whole.
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async token =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            await db.SaveChangesAsync(token);
            await NotifyAsync(db, changed, token);
            // The commit is not tied to the request: once it has started, a cancellation must not leave
            // the outcome unknown to this instance.
            await transaction.CommitAsync(CancellationToken.None);
        }, ct);

        await InvalidateLocalAsync(changed);
    }

    public async Task NotifyAsync(DbContext db, CacheGroup changed, CancellationToken ct = default)
    {
        var payload = Payload(changed);
        await db.Database.ExecuteSqlAsync($"SELECT pg_notify({Channel}, {payload})", ct);
    }

    public Task InvalidateLocalAsync(CacheGroup changed) => cache.InvalidateAsync(changed, CancellationToken.None);
}
