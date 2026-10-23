using System.Net;
using System.Net.Sockets;
using Croft.Api.Services;
using Croft.Api.Tests.Fixtures;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Croft.Api.Tests;

/// <summary>
/// The cache listener must not sit out Npgsql's default 15 s connect timeout when its first
/// connection attempt stalls (a lost SYN, a port forward that swallows the first connection): with
/// that default the second attempt starts after 15 s + 1 s backoff and a slow start costs more
/// than 30 s of un-invalidated cache. The first connection through the proxy below is accepted
/// and never answered; later ones are relayed to the real database.
/// </summary>
public class CacheListenerConnectTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    [Fact]
    public async Task AStalledFirstConnectionAttempt_IsRetriedWithinSeconds_NotAfterTheDefaultFifteenSecondTimeout()
    {
        var real = new NpgsqlConnectionStringBuilder(fixture.ConnectionString);
        using var proxy = new StallFirstConnectionProxy(real.Host!, real.Port);
        var through = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Host = "127.0.0.1", Port = proxy.Port };

        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);
        await using var factory = new BlogApiFactory(
            through.ConnectionString,
            storagePath,
            configureServices: services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<CacheListenerService>());
                CacheListenerDiagnostics.Collect(services);
            });
        var listener = factory.Services.GetRequiredService<CacheListenerService>();

        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            while (listener.BackendPid is null)
            {
                await timer.WaitForNextTickAsync(bound.Token);
            }
        }
        catch (OperationCanceledException)
        {
            false.Should().BeTrue($"the listener connected within 12 s of a stalled first attempt. {CacheListenerDiagnostics.Describe(factory)}");
        }

        listener.BackendPid.Should().NotBeNull();
        proxy.Accepted.Should().BeGreaterThanOrEqualTo(2, "the first attempt stalled and a second one connected");
    }

    private sealed class StallFirstConnectionProxy : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly List<TcpClient> _held = [];
        private int _accepted;

        public StallFirstConnectionProxy(string host, int port)
        {
            _listener.Start();
            _ = AcceptLoopAsync(host, port);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int Accepted => Volatile.Read(ref _accepted);

        private async Task AcceptLoopAsync(string host, int port)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    if (Interlocked.Increment(ref _accepted) == 1)
                    {
                        // Accepted, never read, never answered.
                        lock (_held) { _held.Add(client); }
                        continue;
                    }

                    _ = RelayAsync(client, host, port);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task RelayAsync(TcpClient client, string host, int port)
        {
            using (client)
            using (var upstream = new TcpClient())
            {
                await upstream.ConnectAsync(host, port, _stop.Token);
                var up = client.GetStream().CopyToAsync(upstream.GetStream(), _stop.Token);
                var down = upstream.GetStream().CopyToAsync(client.GetStream(), _stop.Token);
                try
                {
                    await Task.WhenAny(up, down);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException)
                {
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            lock (_held)
            {
                foreach (var client in _held)
                    client.Dispose();
            }

            _stop.Dispose();
        }
    }
}
