namespace Croft.Web;

/// <summary>
/// Brief 11a item 2: <c>.well-known/security.txt</c>'s within-30-days warning is logged again
/// once a day while the expiry stays so, by a hosted service that ticks on
/// <see cref="TimeProvider"/>. The site must not fail startup or readiness over any of the
/// conditions the checker flags — the only effect is a Warning line.
/// </summary>
/// <remarks>
/// The service is a no-op when no <c>security.txt</c> is loaded: <see cref="SecurityTxtChecker"/>
/// seeds the cached body at startup and the daily tick re-evaluates it. Without a file there is
/// nothing to check; the service keeps running so the file can be added (the next site-files
/// folder reload — a ConfigMap change rolls the pods — picks up the new file and the same
/// process re-checks it the next tick).
/// </remarks>
public partial class SecurityTxtHostedService(
    SecurityTxtChecker checker,
    ILogger<SecurityTxtHostedService> logger,
    TimeProvider clock) : BackgroundService
{
    /// <summary>One day, by the configured clock, between ticks. The brief asks for "once a day",
    /// so this is the unit.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await checker.DailyTickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The brief's headline rule: the site must not fail over security.txt. A daily
                // tick that errors for any reason must not crash the host; the next tick will
                // try again.
                LogTickFailed(logger, ex);
            }

            await Task.Delay(TickInterval, clock, stoppingToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The security.txt daily tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);
}