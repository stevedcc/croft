using System.Globalization;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Croft.Shared;

/// <summary>
/// The retry policy of the "migrate" entries (the k8s init containers of Croft.Api and
/// Croft.Web). The database may be restarting when a pod starts (2026-10-17: a CloudNativePG
/// restart made every migrating init container throw an unhandled exception, and a process that
/// ends in an unhandled exception as PID 1 was seen to hang instead of exiting). So the entry
/// never throws: it retries transient failures with a capped exponential backoff until
/// <c>Database:MigrateTimeout</c>, then logs one error and returns exit code 1, which makes
/// Kubernetes restart the init container. A failure that is not transient (a migration that
/// fails) is logged and returned as exit code 1 at once.
/// </summary>
public static partial class MigrationRetry
{
    private const string TimeoutSetting = "Database:MigrateTimeout";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Reads <c>Database:MigrateTimeout</c> (a <see cref="TimeSpan"/>, default 5 minutes). A
    /// present-but-invalid value throws naming the setting, so a typo stops the pod at boot.
    /// </summary>
    public static TimeSpan ReadTimeout(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var raw = configuration[TimeoutSetting];
        if (raw is null)
            return DefaultTimeout;

        if (!TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var value) || value <= TimeSpan.Zero)
            throw new InvalidOperationException($"{TimeoutSetting} must be a positive time span (hh:mm:ss), got '{raw}'.");

        return value;
    }

    /// <summary>
    /// True when the failure, or anything in its inner-exception chain, is one Npgsql classifies
    /// as transient (a refused or dropped connection, a server that is starting up or shutting
    /// down), or a socket or timeout failure. EF's default execution strategy wraps those in an
    /// <see cref="InvalidOperationException"/> ("likely due to a transient failure"), so the
    /// chain is walked, not just the outer exception.
    /// </summary>
    public static bool IsTransient(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is NpgsqlException { IsTransient: true } or SocketException or TimeoutException)
                return true;
        }

        return false;
    }

    /// <summary>
    /// The whole "migrate" entry: reads <c>Database:MigrateTimeout</c> and runs the policy. An
    /// invalid setting is logged and returned as exit code 1 like every other failure, never
    /// thrown.
    /// </summary>
    public static async Task<int> RunEntryAsync(
        IConfiguration configuration, ILogger logger, TimeProvider clock, Func<Task> migrate)
    {
        TimeSpan timeout;
        try
        {
            timeout = ReadTimeout(configuration);
        }
        catch (InvalidOperationException exception)
        {
            LogInvalidSetting(logger, exception);
            return 1;
        }

        return await RunAsync(migrate, timeout, logger, clock);
    }

    /// <summary>
    /// Runs <paramref name="migrate"/> until it succeeds (exit code 0), fails with a
    /// non-transient exception, or the transient failures outlast <paramref name="timeout"/>
    /// (exit code 1, one error logged with the last exception). Never throws a migration
    /// failure. Delays are 1, 2, 4, 8, 16, then 30 seconds, the last one shortened so a final
    /// attempt falls on the timeout itself. The work gets no cancellation token on purpose
    /// (see <see cref="BackgroundJobLock.RunAsync"/>).
    /// </summary>
    public static async Task<int> RunAsync(Func<Task> migrate, TimeSpan timeout, ILogger logger, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(migrate);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(clock);

        var started = clock.GetUtcNow();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await migrate();
                return 0;
            }
            catch (Exception exception)
            {
                if (!IsTransient(exception))
                {
                    LogNonTransient(logger, exception);
                    return 1;
                }

                var remaining = timeout - (clock.GetUtcNow() - started);
                if (remaining <= TimeSpan.Zero)
                {
                    LogGaveUp(logger, exception, timeout, TimeoutSetting, attempt);
                    return 1;
                }

                var delay = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt - 1), MaxDelay.TotalSeconds));
                if (delay > remaining)
                    delay = remaining;

                LogRetry(logger, attempt, exception.GetBaseException().Message, (int)Math.Ceiling(delay.TotalSeconds));
                await Task.Delay(delay, clock);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Migration not started: the migrate timeout setting is invalid.")]
    private static partial void LogInvalidSetting(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Migration failed with a non-transient error; not retrying.")]
    private static partial void LogNonTransient(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "The database stayed unreachable for the {Timeout} migrate timeout ({Setting}); giving up after {Attempts} attempts.")]
    private static partial void LogGaveUp(ILogger logger, Exception exception, TimeSpan timeout, string setting, int attempts);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Migration attempt {Attempt} failed with a transient error ({Error}); retrying in {DelaySeconds}s.")]
    private static partial void LogRetry(ILogger logger, int attempt, string error, int delaySeconds);
}
