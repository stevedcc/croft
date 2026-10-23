using Croft.Shared;
using Croft.Web.Data;
using Croft.Web.Options;
using Microsoft.EntityFrameworkCore;

namespace Croft.Web.Services;

/// <summary>
/// Purges ended sessions from the shared store: rows whose session expiry (the refresh token's
/// lifetime, not the access token's) has passed are dead weight nobody will read again, and without a purge the table only ever grows. Every Web
/// instance runs the tick; the job's advisory lock makes one of them do the work and the others
/// skip it, so the purge happens exactly once per tick across the deployment.
/// </summary>
public partial class SessionPurgeHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<SessionPurgeHostedService> logger,
    TimeProvider time,
    AuthSettings authSettings) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<CroftWebDbContext>();
                await PurgeAsync(db, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogSessionPurgeFailed(logger, ex);
            }

            await Task.Delay(TimeSpan.FromMinutes(1), time, stoppingToken);
        }
    }

    public async Task<SessionPurgeResult> PurgeAsync(CroftWebDbContext db, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        // A row written by a release that did not know the column has no session expiry: it ends
        // when its access token expired longer ago than a session may live.
        var legacyCutoff = now - authSettings.SessionLifetime;
        var purged = 0;
        var ran = await BackgroundJobLock.TryRunAsync(db, BackgroundJobIds.SessionPurge, async token =>
        {
            purged = await db.UserSessions
                .Where(s => s.SessionExpiresAt < now
                    || (s.SessionExpiresAt == null && s.ExpiresAt < legacyCutoff))
                .ExecuteDeleteAsync(token);
            SessionRemoval.LogPurged(logger, purged);
        }, ct);

        return new SessionPurgeResult(Skipped: !ran, Purged: purged);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The expired-session purge failed.")]
    private static partial void LogSessionPurgeFailed(ILogger logger, Exception exception);
}
