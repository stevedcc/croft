namespace Croft.Web.Services;

/// <summary>
/// The in-memory token store's counterpart of <see cref="SessionPurgeHostedService"/>: once a
/// minute, entries whose session has ended are removed, so a long-running single-instance Web does
/// not collect sessions until it restarts. (An expired entry is already invisible to the store's
/// reads; this frees the memory.)
/// </summary>
public sealed class InMemorySessionPurgeHostedService(
    InMemoryTokenStore store,
    ILogger<InMemorySessionPurgeHostedService> logger,
    TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var purged = store.PurgeExpired();
            if (purged > 0)
            {
                SessionRemoval.LogPurged(logger, purged);
            }

            await Task.Delay(TimeSpan.FromMinutes(1), time, stoppingToken);
        }
    }
}
