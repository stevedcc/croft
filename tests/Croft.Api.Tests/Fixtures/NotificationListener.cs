using Npgsql;

namespace Croft.Api.Tests.Fixtures;

/// <summary>
/// A LISTEN connection of its own on one channel that collects every payload it is sent, in
/// order. <see cref="WaitForSentinelAsync"/> makes "nothing else arrived" checkable without a
/// sleep: Postgres delivers notifications in commit order, so once a sentinel sent after the
/// write under test has arrived, everything the write sent has arrived too.
/// </summary>
internal sealed class NotificationListener : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly string _connectionString;
    private readonly string _channel;
    private readonly List<string> _payloads = [];
    private readonly CancellationTokenSource _stop = new();
    private Task _pump = Task.CompletedTask;
    private TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private NotificationListener(string connectionString, string channel)
    {
        _connectionString = connectionString;
        _channel = channel;
        _connection = new NpgsqlConnection(connectionString);
    }

    public static async Task<NotificationListener> StartAsync(string connectionString, string channel)
    {
        var listener = new NotificationListener(connectionString, channel);
        await listener._connection.OpenAsync();
        listener._connection.Notification += listener.OnNotification;
        await using (var listen = listener._connection.CreateCommand())
        {
            listen.CommandText = $"LISTEN {channel}";
            await listen.ExecuteNonQueryAsync();
        }

        listener._pump = Task.Run(async () =>
        {
            try
            {
                while (!listener._stop.IsCancellationRequested)
                    await listener._connection.WaitAsync(listener._stop.Token);
            }
            catch (OperationCanceledException)
            {
                // disposed
            }
        });
        return listener;
    }

    /// <summary>Every payload received so far, sentinel included.</summary>
    public IReadOnlyList<string> Payloads
    {
        get { lock (_payloads) return [.. _payloads]; }
    }

    /// <summary>Sends a sentinel from another connection and waits for it; returns everything received before it.</summary>
    public async Task<IReadOnlyList<string>> WaitForSentinelAsync()
    {
        const string Sentinel = "sentinel";
        await using (var sender = new NpgsqlConnection(_connectionString))
        {
            await sender.OpenAsync();
            await using var command = sender.CreateCommand();
            command.CommandText = "SELECT pg_notify(@channel, @payload)";
            command.Parameters.AddWithValue("channel", _channel);
            command.Parameters.AddWithValue("payload", Sentinel);
            await command.ExecuteScalarAsync();
        }

        while (true)
        {
            var arrived = Volatile.Read(ref _arrived);
            var received = Payloads;
            var at = received.ToList().IndexOf(Sentinel);
            if (at >= 0)
                return [.. received.Take(at)];
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    private void OnNotification(object sender, NpgsqlNotificationEventArgs e)
    {
        lock (_payloads)
            _payloads.Add(e.Payload);
        var previous = Interlocked.Exchange(ref _arrived, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        previous.TrySetResult();
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _pump;
        await _connection.DisposeAsync();
        _stop.Dispose();
    }
}
