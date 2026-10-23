using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Croft.Api.Data;
using Croft.Api.Errors;
using Croft.Api.Telemetry;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Microsoft.EntityFrameworkCore;

namespace Croft.Api.Services;

public class AnalyticsService(BlogDbContext db, IConfiguration configuration, TimeProvider time, DateTimeNormalizer normalizer) : IAnalyticsService
{
    /// <summary>The longest referrer stored: a longer one is cut, not rejected, so the view still counts.</summary>
    public const int MaxReferrerLength = 2048;

    /// <summary>The longest User-Agent stored (cut the same way).</summary>
    public const int MaxUserAgentLength = 512;

    private readonly byte[] _hashKey = Encoding.UTF8.GetBytes(configuration["Analytics:HashKey"] ?? string.Empty);

    public async Task RecordPageViewAsync(Guid postId, string? ipAddress, string? userAgent, string? referrer, CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var day = DateOnly.FromDateTime(now);

        // Only published posts whose publish time has passed accumulate views.
        var countable = await db.Posts.AnyAsync(
            p => p.Id == postId && p.Status == PostStatus.Published && p.PublishedAt <= now,
            ct);
        if (!countable)
            return;

        // The columns are unbounded text for now (expand/contract: a pending contract adds the
        // limits in the next release), so the bound is enforced here; a beacon is anonymous.
        userAgent = Truncate(userAgent, MaxUserAgentLength);
        referrer = Truncate(referrer, MaxReferrerLength);

        if (await InsertPageViewOnceAsync(postId, now, day, HashIp(ipAddress ?? "unknown", day), userAgent, referrer, ct) == 0)
        {
            // Same post, visitor and day already recorded: the unique index did the dedupe.
            ApiMetrics.PageViewDuplicate();
            return;
        }

        ApiMetrics.PageViewStored();

        await IncrementViewCountAsync(postId, ct);
    }

    // WORKAROUND(W-093, EF Core): EF has no INSERT ... ON CONFLICT DO UPDATE, so the first view creates the count row and later ones increment it in one raw statement
    /// <summary>
    /// Counts one view in the narrow PostViewCounts table, never on the Posts row (a view used to
    /// rewrite that wide, heavily indexed row). One statement, atomic: the first view inserts the row
    /// (1), every later one adds to it, so concurrent views lose no update and never collide on the key.
    /// Table and column names come from the EF model, like the page-view insert.
    /// </summary>
    private async Task IncrementViewCountAsync(Guid postId, CancellationToken ct)
    {
        var sql = FormattableStringFactory.Create(AnalyticsTables.For(db.Model).IncrementViewCount, postId);

        await db.Database.ExecuteSqlAsync(sql, ct);
    }
    // END WORKAROUND(W-093)

    /// <summary>The first <paramref name="maxLength"/> characters; a cut that would leave half a
    /// surrogate pair drops that half too (a lone surrogate cannot be stored as UTF-8).</summary>
    private static string? Truncate(string? value, int maxLength)
    {
        if (value is null || value.Length <= maxLength)
            return value;

        var length = char.IsHighSurrogate(value[maxLength - 1]) ? maxLength - 1 : maxLength;
        return value[..length];
    }

    // WORKAROUND(W-031, EF Core): EF has no ON CONFLICT DO NOTHING; a failed insert through SaveChanges is logged as a Postgres error
    /// <summary>
    /// Inserts the page view with INSERT ... ON CONFLICT DO NOTHING on the unique
    /// (PostId, IpHash, Day) index, so a repeat view is a silent no-op instead of a failed
    /// command that the database logs as an error. Table and column names come from the EF
    /// model. Returns 1 when the row was inserted, 0 when that visitor already has a view
    /// for the post and day.
    /// </summary>
    private async Task<int> InsertPageViewOnceAsync(Guid postId, DateTime timestamp, DateOnly day, string ipHash, string? userAgent, string? referrer, CancellationToken ct)
    {
        // Only the values become SQL parameters; the identifiers are quoted and inlined, once per model.
        // The id is supplied here because the row no longer goes through SaveChanges.
        var sql = FormattableStringFactory.Create(
            AnalyticsTables.For(db.Model).InsertPageView,
            Guid.CreateVersion7(), postId, timestamp, day, ipHash, userAgent, referrer);

        return await db.Database.ExecuteSqlAsync(sql, ct);
    }
    // END WORKAROUND(W-031)

    public async Task<AnalyticsSummaryDto> GetSummaryAsync(Guid postId, DateTime? from = null, DateTime? to = null, CancellationToken ct = default)
    {
        var post = await db.Posts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == postId, ct);
        if (post is null)
            throw new NotFoundException($"Post {postId} not found.");

        var now = time.GetUtcNow().UtcDateTime;
        from = normalizer.NormalizeToUtc(from ?? now.AddDays(-30));
        to = normalizer.NormalizeToUtc(to ?? now);

        // Group by Day in SQL: per-day totals only, never every row in memory.
        var dailyRows = await db.PageViews
            .AsNoTracking()
            .Where(p => p.PostId == postId && p.Timestamp >= from && p.Timestamp <= to)
            .GroupBy(p => p.Day)
            .Select(g => new { Day = g.Key, Count = g.LongCount() })
            .OrderBy(r => r.Day)
            .ToListAsync(ct);

        var daily = dailyRows
            .Select(r => new DailyViewCount(r.Day.ToDateTime(TimeOnly.MinValue), r.Count))
            .ToList();

        var totalViews = await db.PostViewCounts.AsNoTracking()
            .Where(v => v.PostId == postId)
            .Select(v => v.ViewCount)
            .SingleOrDefaultAsync(ct);

        return new AnalyticsSummaryDto(postId, post.Title, totalViews, daily);
    }

    private string HashIp(string ip, DateOnly day)
    {
        // Date-salted HMAC: hashes of one visitor cannot be linked across days.
        // Outside Development the key is guaranteed by the startup check in Program.cs.
        var message = Encoding.UTF8.GetBytes($"{day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}|{ip}");
        return Convert.ToHexString(HMACSHA256.HashData(_hashKey, message));
    }
}
