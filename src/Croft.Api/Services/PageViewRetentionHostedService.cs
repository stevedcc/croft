using System.Runtime.CompilerServices;
using Croft.Api.Data;
using Croft.Shared;
using Microsoft.EntityFrameworkCore;

namespace Croft.Api.Services;

/// <summary>Result of one retention run: whether this instance held the job lock and how much it deleted.</summary>
public sealed record PageViewRetentionResult(bool Skipped, int Deleted);

/// <summary>
/// Deletes PageViews rows whose Day is more than Croft:Analytics:RetentionDays (default 90) days
/// before today — the privacy page promises the deletion, so the rows must not outlive it. Runs
/// once a day, in batches of at most 5,000 rows per statement, under the background job lock:
/// one replica deletes and the others skip. PostViewCounts is not touched: per-post totals stay.
/// </summary>
public sealed partial class PageViewRetentionHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<PageViewRetentionHostedService> logger,
    TimeProvider time,
    IConfiguration configuration) : BackgroundService
{
    /// <summary>The most rows one DELETE statement removes: a big purge must not become one huge statement.</summary>
    private const int BatchSize = 5000;

    private readonly int retentionDays = configuration.GetValue("Croft:Analytics:RetentionDays", 90);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
                await PurgeAsync(db, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogPageViewRetentionFailed(logger, ex);
            }

            await Task.Delay(TimeSpan.FromHours(24), time, stoppingToken);
        }
    }

    public async Task<PageViewRetentionResult> PurgeAsync(BlogDbContext db, CancellationToken ct = default)
    {
        // "More than RetentionDays days before today": with a 90-day retention the row on day -90
        // is exactly 90 days old and stays; day -91 goes.
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var cutoff = today.AddDays(-retentionDays);

        var deleted = 0;
        var ran = await BackgroundJobLock.TryRunAsync(db, BackgroundJobIds.PageViewRetention, async token =>
        {
            int batch;
            do
            {
                batch = await db.Database.ExecuteSqlAsync(
                    FormattableStringFactory.Create(AnalyticsTables.For(db.Model).PurgePageViews, cutoff, BatchSize), token);
                deleted += batch;
            }
            while (batch == BatchSize);

            if (deleted > 0)
                LogDeletedOldPageViews(logger, deleted);
        }, ct);

        return new PageViewRetentionResult(Skipped: !ran, Deleted: deleted);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The page-view retention purge failed.")]
    private static partial void LogPageViewRetentionFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Deleted} page-view row(s) past the retention window.")]
    private static partial void LogDeletedOldPageViews(ILogger logger, int deleted);
}
