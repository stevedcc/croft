using Croft.Api.Data;
using Croft.Api.Social;
using Croft.Api.Telemetry;
using Croft.Shared;
using Croft.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Croft.Api.Services;

public partial class ScheduledPublisherHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<ScheduledPublisherHostedService> logger,
    TimeProvider time,
    IScheduledPublishWakeup wakeup,
    ICacheNotifier cacheNotifier,
    IOptions<SocialOptions>? social = null) : BackgroundService
{

    /// <summary>
    /// The longest the scheduler ever sleeps: it covers posts another replica scheduled (their
    /// wakeup never reaches this process) and a replica that died before its post was due.
    /// </summary>
    private static readonly TimeSpan MaxSleep = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Captured before the tick: a wake that fires while the tick runs completes this
            // task, so the sleep after the tick sees it instead of sleeping past it.
            var nextWake = wakeup.Next;

            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
                await PublishDuePostsAsync(db, stoppingToken);

                // Sleep until the earliest scheduled post is due, capped: with nothing
                // scheduled that is simply the cap.
                var earliest = await db.Posts
                    .AsNoTracking()
                    .Where(p => p.Status == PostStatus.Scheduled)
                    .MinAsync(p => (DateTime?)p.PublishedAt, stoppingToken);

                var now = time.GetUtcNow().UtcDateTime;
                var delay = earliest is { } due
                    ? TimeSpan.FromTicks(Math.Clamp((due - now).Ticks, 0, MaxSleep.Ticks))
                    : MaxSleep;

                await SleepUntilAsync(delay, nextWake, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogScheduledPublisherFailed(logger, ex);
                await Task.Delay(TimeSpan.FromMinutes(1), time, stoppingToken);
            }
        }
    }

    /// <summary>
    /// Sleeps for <paramref name="delay"/> on the injected clock, waking early when
    /// <paramref name="nextWake"/> completes (a post was scheduled, rescheduled or
    /// unscheduled). The losing side is cancelled so an abandoned sleep holds no timer.
    /// Shutdown propagates through the timer task, exactly as a plain Task.Delay would.
    /// </summary>
    private async Task SleepUntilAsync(TimeSpan delay, Task nextWake, CancellationToken stoppingToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            return; // Something became due while this tick ran: loop straight back around.
        }

        using var sleep = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var timer = Task.Delay(delay, time, sleep.Token);
        var completed = await Task.WhenAny(timer, nextWake);
        sleep.Cancel();
        await completed;
    }

    public async Task<PublishDuePostsResult> PublishDuePostsAsync(BlogDbContext db, CancellationToken ct = default)
    {
        // WORKAROUND(W-030, Npgsql): Postgres timestamps have microsecond resolution but .NET ticks are 100 ns, so the stamp is truncated
        // Postgres timestamps have microsecond resolution: trim the tick's stamp to match, so the
        // rows this tick writes can be recognised again after the write round-trips to the database.
        var now = time.GetUtcNow().UtcDateTime;
        now = now.AddTicks(-(now.Ticks % 10));
        // END WORKAROUND(W-030)

        List<PublishedPost> published = [];
        var ran = await BackgroundJobLock.TryRunAsync(db, BackgroundJobIds.ScheduledPublisher, async token =>
        {
            var duePosts = await db.Posts
                .Where(p => p.Status == PostStatus.Scheduled && p.PublishedAt <= now)
                .Select(p => new { p.Id, p.Slug })
                .ToListAsync(token);

            if (duePosts.Count == 0)
                return;

            var dueIds = duePosts.Select(p => p.Id).ToList();

            // Conditional on the row still being due and bounded to the rows just read: even with
            // a broken job lock this cannot publish a post twice.
            await db.Posts
                .Where(p => dueIds.Contains(p.Id) && p.Status == PostStatus.Scheduled && p.PublishedAt <= now)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(p => p.Status, PostStatus.Published)
                    .SetProperty(p => p.PublishedAt, p => p.PublishedAt ?? now)
                    .SetProperty(p => p.UpdatedAt, now), token);

            // Only rows stamped with this tick's now were changed by it. A row another instance
            // published while this tick was reading carries that instance's stamp instead and is
            // neither logged nor reported as ours.
            var publishedNow = await db.Posts
                .Where(p => dueIds.Contains(p.Id) && p.Status == PostStatus.Published && p.UpdatedAt == now)
                .Select(p => new { p.Id, p.Slug })
                .ToListAsync(token);

            // The rows are inserted here, in this transaction (ExecuteUpdate above bypasses the entity tracking, so nothing queued them): they
            // exist if and only if the publish commits.
            await new AnnouncementOutbox(db, social ?? Options.Create(new SocialOptions()))
                .QueueAsync([.. publishedNow.Select(p => p.Id)], now, token);
            await db.SaveChangesAsync(token);

            foreach (var post in publishedNow)
            {
                published.Add(new PublishedPost(post.Id, post.Slug));
                ApiMetrics.PostPublishedByScheduler();
                LogPublishedScheduledPost(logger, post.Id, post.Slug);
            }

            // A publish can change a category or tag count: the NOTIFY joins this transaction, so
            // every replica hears of it when (and only if) the publish commits.
            if (published.Count > 0)
                await cacheNotifier.NotifyAsync(db, CacheGroups.PostVisibility, token);
        }, ct);

        // TryRunAsync committed its transaction when it returned: this instance's cache follows.
        if (published.Count > 0)
            await cacheNotifier.InvalidateLocalAsync(CacheGroups.PostVisibility);

        return new PublishDuePostsResult(Skipped: !ran, Published: [.. published]);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Scheduled publisher failed.")]
    private static partial void LogScheduledPublisherFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Published scheduled post {PostId} ({Slug})")]
    private static partial void LogPublishedScheduledPost(ILogger logger, Guid postId, string slug);
}
