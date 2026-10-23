// WORKAROUND-FILE(W-022, Npgsql): notifications arrive only while the connection waits, are lost while it is down, and the event is synchronous
using System.Globalization;
using System.Threading.Channels;
using Croft.Api.Telemetry;
using Npgsql;

namespace Croft.Api.Services;

/// <summary>
/// Per-replica LISTEN/NOTIFY bridge for the public read cache: it listens on the
/// <see cref="CacheNotifier.Channel"/> channel on its own dedicated connection (never EF's pool)
/// and clears the matching local entries on every notification — its own replica's included;
/// clearing twice is harmless. When the connection drops, notifications sent while it was down
/// are lost, so every (re)connect clears the whole cache first.
/// </summary>
public sealed partial class CacheListenerService(
    IPublicReadCache cache,
    IConfiguration configuration,
    ILogger<CacheListenerService> logger) : BackgroundService
{
    private const string Channel = CacheNotifier.Channel;
    private const string ApplicationName = "croft-cache-listener";

    private const int ConnectTimeoutSeconds = 5;

    private static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private bool _everConnected;

    /// <summary>
    /// Hand-off from the synchronous notification event to the drain loop: one group per
    /// notification, unbounded so a burst is queued in full and never dropped.
    /// </summary>
    private readonly Channel<CacheGroup> _invalidations = System.Threading.Channels.Channel.CreateUnbounded<CacheGroup>();

    /// <summary>The Postgres backend id of the LISTEN connection, once setup is complete.</summary>
    public int? BackendPid { get; private set; }

    /// <summary>The last connect or setup failure while the listener is not connected (null once
    /// connected); a test that waits for the listener reports it when the wait runs out.</summary>
    public string? LastError { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The drain loop applies invalidations off the notification event; it starts and
        // stops with the service.
        var invalidations = DrainInvalidationsAsync(stoppingToken);
        using var observed = ApiMetrics.ObserveCacheListener(() => BackendPid is not null);
        var backoff = FirstBackoff;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = new NpgsqlConnection(BuildConnectionString(configuration));
                await connection.OpenAsync(stoppingToken);
                connection.Notification += OnNotification;

                await using (var listen = connection.CreateCommand())
                {
                    listen.CommandText = $"LISTEN {Channel}";
                    await listen.ExecuteNonQueryAsync(stoppingToken);
                }

                // From LISTEN on every notification is delivered; anything sent before is lost
                // and must not survive in the cache.
                await cache.ClearAsync(stoppingToken);

                await using (var pid = connection.CreateCommand())
                {
                    pid.CommandText = "SELECT pg_backend_pid()";
                    BackendPid = Convert.ToInt32(await pid.ExecuteScalarAsync(stoppingToken), CultureInfo.InvariantCulture);
                }

                if (_everConnected)
                {
                    ApiMetrics.CacheListenerReconnected();
                    LogReconnected(logger, BackendPid.Value);
                }
                else
                    LogConnected(logger, BackendPid.Value);
                _everConnected = true;
                LastError = null;
                backoff = FirstBackoff;

                while (!stoppingToken.IsCancellationRequested)
                    await connection.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                BackendPid = null;
                LastError = $"{ex.GetType().Name}: {ex.Message}";
                LogDisconnected(logger, ex);
                if (await DelayAsync(backoff, stoppingToken))
                    break;
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, MaxBackoff.TotalSeconds));
            }
        }

        await invalidations;
    }

    /// <summary>
    /// Applies one cache invalidation per notification, in order — so a burst of
    /// notifications is applied in full — awaiting the cache instead of blocking. The
    /// notification event only hands groups over; nothing anywhere waits synchronously.
    /// </summary>
    private async Task DrainInvalidationsAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var group in _invalidations.Reader.ReadAllAsync(stoppingToken))
            {
                await cache.InvalidateAsync(group, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutdown: the channel reader ends with the service
        }
    }

    private void OnNotification(object sender, NpgsqlNotificationEventArgs e)
    {
        // One notification names one or several groups (see CacheNotifier.Payload).
        if (!CacheNotifier.TryParsePayload(e.Payload, out var groups))
        {
            LogUnknownPayload(logger, e.Payload);
            return;
        }

        _invalidations.Writer.TryWrite(groups);
    }

    /// <summary>The connection string of the LISTEN connection (public so a test can pin it).</summary>
    public static string BuildConnectionString(IConfiguration configuration)
    {
        // The shared builder applies Database:MaxPoolSize / Database:MaxAutoPrepare; the
        // listener then keeps its own Application Name and Keep Alive, and no pool at all.
        var builder = Croft.Api.Data.BlogDbConnectionString.Build(configuration);
        builder.ApplicationName = ApplicationName;
        builder.KeepAlive = 15;
        // No pool: this is one dedicated, long-lived LISTEN connection, which gains nothing from
        // pooling. Brief 13f's evidence: a connector returned to the pool (after DISCARD ALL) and
        // taken again by a new listener got no reply to its first command for the full 30 s command
        // timeout under load (the backend idle, the LISTEN never arriving); with Pooling=false 16
        // stress loops passed. A reconnect after a failure also must not pick up a pooled connector
        // of the dead connection's pool.
        builder.Pooling = false;
        // A connect attempt that stalls (a swallowed SYN, a port forward that never answers) is
        // abandoned after ConnectTimeoutSeconds, not Npgsql's default 15 s: with 15 s plus the
        // 1 s first backoff the second attempt would start after 16 s.
        builder.Timeout = ConnectTimeoutSeconds;
        return builder.ConnectionString;
    }

    /// <summary>True when the wait was cut short by shutdown.</summary>
    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, stoppingToken);
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Cache listener connected (backend {BackendPid}).")]
    private static partial void LogConnected(ILogger logger, int backendPid);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cache listener reconnected (backend {BackendPid}); the whole cache is cleared.")]
    private static partial void LogReconnected(ILogger logger, int backendPid);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cache listener disconnected; reconnecting with backoff.")]
    private static partial void LogDisconnected(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignoring unknown cache invalidation payload {Payload}.")]
    private static partial void LogUnknownPayload(ILogger logger, string payload);
}
