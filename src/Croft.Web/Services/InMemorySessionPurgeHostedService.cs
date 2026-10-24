namespace Croft.Web.Services;

/// <summary>
/// The in-memory token store's counterpart of <see cref="SessionPurgeHostedService"/>: once a
/// minute, entries whose session has ended are removed, so a long-running single-instance Web does
/// not collect sessions until it restarts. (An expired entry is already invisible to the store's
/// reads; this frees the memory.)
/// </summary>
public sealed partial class InMemorySessionPurgeHostedService(
    InMemoryTokenStore store,
    ILogger<InMemorySessionPurgeHostedService> logger,
    TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // A failed tick is logged and the next one tries again: an exception that escaped
            // ExecuteAsync would stop the host (and since .NET 11 make it exit with a failure).
            try
            {
                var purged = store.PurgeExpired();
                if (purged > 0)
                {
                    SessionRemoval.LogPurged(logger, purged);
                }
            }
            catch (Exception ex)
            {
                LogSessionPurgeFailed(logger, ex);
            }

            await Task.Delay(TimeSpan.FromMinutes(1), time, stoppingToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The in-memory expired-session purge failed.")]
    private static partial void LogSessionPurgeFailed(ILogger logger, Exception exception);
}
