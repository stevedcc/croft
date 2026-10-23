using Croft.Api.Data;
using Croft.Shared;
using Microsoft.EntityFrameworkCore;

namespace Croft.Api.Services;

/// <summary>
/// Fills Summary/ReadingMinutes for posts created before summary extraction existed
/// (Summary == null). Runs once at startup in batches of 100 and never touches UpdatedAt,
/// so the backfill is invisible in the post's edit history.
/// </summary>
public partial class PostSummaryBackfillService(IServiceScopeFactory scopeFactory, ILogger<PostSummaryBackfillService> logger, ICacheNotifier cacheNotifier) : BackgroundService
{
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
            await RunOnceAsync(db, stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogBackfillFailed(logger, ex);
        }
    }

    public async Task<PostSummaryBackfillResult> RunOnceAsync(BlogDbContext db, CancellationToken ct = default)
    {
        var filled = 0;
        var ran = await BackgroundJobLock.TryRunAsync(db, BackgroundJobIds.PostSummaryBackfill, async token =>
        {
            // The whole run is one transaction (BackgroundJobLock.TryRunAsync commits when the
            // tick ends): a first run against an existing database therefore holds row locks on
            // every Posts row it updates until the very end. Fine at today's post counts; if they
            // ever grow large, the lever is committing per batch here instead of once per run.
            while (true)
            {
                var batch = await db.Posts
                    .Where(p => p.Summary == null)
                    .OrderBy(p => p.CreatedAt)
                    .Select(p => new { p.Id, p.MarkdownContent })
                    .Take(BatchSize)
                    .ToListAsync(token);

                if (batch.Count == 0)
                    break;

                foreach (var post in batch)
                {
                    var (summary, readingMinutes) = PostSummaryExtractor.Extract(post.MarkdownContent);

                    // Conditional on the summary still missing: one another instance filled in
                    // while this run was reading wins and is never overwritten. A row changed
                    // here counts towards what this run filled.
                    filled += await db.Posts
                        .Where(p => p.Id == post.Id && p.Summary == null)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(p => p.Summary, summary)
                            .SetProperty(p => p.ReadingMinutes, readingMinutes), token);
                }
            }

            // The backfill writes content shown in DTOs, so it honours the one rule and
            // invalidates like every other content write. The NOTIFY joins this transaction:
            // every instance hears of the summaries when (and only if) they commit.
            if (filled > 0)
                await cacheNotifier.NotifyAsync(db, CacheGroup.All, token);
        }, ct);

        // TryRunAsync committed its transaction when it returned: the point where the filled
        // summaries become visible on this instance too.
        if (filled > 0)
            await cacheNotifier.InvalidateLocalAsync(CacheGroup.All);

        return new PostSummaryBackfillResult(Skipped: !ran, Filled: filled);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Post summary backfill failed.")]
    private static partial void LogBackfillFailed(ILogger logger, Exception exception);
}
