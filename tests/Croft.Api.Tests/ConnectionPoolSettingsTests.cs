using System.Globalization;
using Croft.Api.Tests.Fixtures;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Croft.Api.Tests;

/// <summary>
/// Brief 09d item 1: Database:MaxPoolSize caps Npgsql's pool per process, so concurrent readers
/// queue for connections instead of opening Postgres backends until the server's max_connections
/// says no (the 2026-10-15 load test broke at "53300 too many clients" with the default pool).
/// </summary>
public class ConnectionPoolSettingsTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    [Fact]
    public async Task FourConcurrentHoldingRequests_WithMaxPoolSize3_OpenExactly3BackendsAndStarveTheFourth()
    {
        var applicationName = $"pool-settings-{Guid.NewGuid():N}";
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            ApplicationName = applicationName
        }.ConnectionString;

        var gate = new HoldingCommandInterceptor(holdCount: 3);
        await using var factory = CreateFactory(connectionString, gate);
        var client = factory.CreateClient();

        gate.Arm();
        var requests = Enumerable.Range(1, 4)
            .Select(page => client.GetAsync($"/api/posts?page={page}&pageSize=1"))
            .ToList();

        // Three of the four requests are now sitting in a held command, each on its own open
        // connection; the fourth is waiting for a pool slot.
        await gate.Held.WaitAsync(TimeSpan.FromSeconds(30));

        var backends = await CountBackendsAsync(applicationName);
        backends.Should().Be(3,
            "Database:MaxPoolSize=3 caps the pool at 3 physical connections no matter how many requests want one");

        // With three commands pinned the fourth request cannot get a connection, so its SQL
        // never reaches the gate. Waiting on that concrete condition (bounded) is what makes
        // the break-it-once check deterministic instead of racing the backend count: a pool
        // without the cap hands out a fourth connection and the arrival surfaces here.
        var fourthRanSql = await WaitForArrivalsAsync(gate, 4, TimeSpan.FromSeconds(2));
        fourthRanSql.Should().BeFalse("with the pool capped at 3 the fourth request has no connection to run SQL on");

        gate.Release();
        var responses = await Task.WhenAll(requests);
        responses.Should().OnlyContain(response => response.IsSuccessStatusCode,
            "every request completes once the held commands are released");
    }

    private static async Task<bool> WaitForArrivalsAsync(HoldingCommandInterceptor gate, int arrivals, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (gate.Arrived >= arrivals)
                return true;
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        return gate.Arrived >= arrivals;
    }

    [Fact]
    public void BadPoolSetting_FailsAtStartup_NamingTheSetting()
    {
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            ApplicationName = $"pool-settings-bad-{Guid.NewGuid():N}"
        }.ConnectionString;
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);

        using var factory = new BlogApiFactory(
            connectionString,
            storagePath,
            configuration: new Dictionary<string, string?> { ["Database:MaxPoolSize"] = "" });

        Func<HttpClient> act = () => factory.CreateClient();

        var exception = act.Should().Throw<Exception>(
            "a malformed pool setting must stop the app at startup instead of failing every request").Which;
        Messages(exception).Should().Contain(
            message => message.Contains("Database:MaxPoolSize", StringComparison.Ordinal),
            "the error must name the setting an operator has to fix");
    }

    private static IEnumerable<string> Messages(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            yield return current.Message;
        }
    }

    private static BlogApiFactory CreateFactory(string connectionString, DbCommandInterceptor interceptor)
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);

        return new BlogApiFactory(
            connectionString,
            storagePath,
            configuration: new Dictionary<string, string?> { ["Database:MaxPoolSize"] = "3" },
            // Background services would take connections from the same pool on their own
            // schedule; this test owns every connection its pool hands out.
            configureServices: services => services.RemoveAll<IHostedService>(),
            commandInterceptor: interceptor);
    }

    private async Task<int> CountBackendsAsync(string applicationName)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var count = connection.CreateCommand();
        count.CommandText = """
            SELECT count(*) FROM pg_stat_activity
            WHERE application_name = @name AND datname = current_database()
            """;
        count.Parameters.AddWithValue("@name", applicationName);
        return Convert.ToInt32(await count.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }
}
