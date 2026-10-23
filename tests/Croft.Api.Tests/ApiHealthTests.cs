using System.Net;
using System.Net.Sockets;
using Croft.Api.Tests.Fixtures;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Croft.Api.Tests;

/// <summary>
/// Brief 13c item 5 (architecture finding 7): /health/live answers for the process alone,
/// /health/ready asks the primary database with a short timeout, and /health stays as an alias of
/// ready. An API that cannot reach its database is unready (no traffic) but never restarted.
/// </summary>
public class ApiHealthTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    // Nothing listens on 127.0.0.1:1: the connect is refused at once.
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=unreachable;Username=none;Password=none";

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/health")]
    public async Task WithTheDatabaseReachable_EveryHealthEndpointIs200(string path)
    {
        await using var factory = CreateFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("/health/ready")]
    [InlineData("/health")]
    public async Task WithTheDatabaseUnreachable_ReadyAndTheAliasAre503(string path)
    {
        await using var factory = CreateFactory(UnreachableDatabase);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "an API that cannot open its primary connection takes no traffic");
    }

    [Fact]
    public async Task WithTheDatabaseUnreachable_LiveIsStill200()
    {
        await using var factory = CreateFactory(UnreachableDatabase);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK, "a restart cannot fix a database: liveness is the process alone");
    }

    [Fact]
    public async Task ADatabaseThatNeverAnswers_MakesReadyUnhealthyWithinItsShortTimeout()
    {
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        var port = ((IPEndPoint)silent.LocalEndpoint).Port;
        var accepting = Task.Run(async () =>
        {
            // Accept and hold every connection without ever answering the handshake.
            var held = new List<TcpClient>();
            try
            {
                while (true)
                    held.Add(await silent.AcceptTcpClientAsync());
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException or InvalidOperationException)
            {
                // listener stopped
            }
            finally
            {
                foreach (var client in held)
                    client.Dispose();
            }
        });
        var connection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Host = "127.0.0.1", Port = port };
        await using var factory = CreateFactory(connection.ConnectionString);
        using var client = factory.CreateClient();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var response = await client.GetAsync("/health/ready");
        clock.Stop();
        silent.Stop();
        await accepting;

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(8), "the check gives up after 2 s, inside the kubelet's probe timeout");
    }

    private static BlogApiFactory CreateFactory(string connectionString)
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);
        return new BlogApiFactory(
            connectionString,
            storagePath,
            configureServices: services => services.RemoveAll<IHostedService>());
    }
}
