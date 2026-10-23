using Croft.Web.Data;
using Croft.Web.Services;
using Croft.Web.Tests.Fixtures;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Croft.Web.Tests;

/// <summary>
/// Two migrate runs against one empty database at once — two Web instances starting together, or
/// an init container racing an instance. Both must complete and the schema must end up migrated
/// exactly once. This is what the migrate entry's advisory lock buys: EF Core commits each
/// migration in its own transaction and Npgsql's migration lock is transaction-scoped (a
/// LOCK TABLE on the history table), so raw concurrent Migrate runs over a chain of two or more
/// pending migrations collide (observed on the API: 42701 "column ... already exists"). The
/// collision needs the losing run to re-read the migration history between the winner's
/// migrations, which is otherwise a scheduling race — so the overlap is forced to that exact
/// shape by <see cref="MidChainRendezvousInterceptor"/> (this is the test's rendezvous INPUT;
/// its assertions are unchanged). The test then fails against a lock-less runner and passes
/// against the serialising entry, deterministically.
/// </summary>
[Trait("Category", "Container")]
public class ConcurrentWebMigrationTests(PostgresDatabaseFixture fixture)
    : IClassFixture<PostgresDatabaseFixture>
{
    [Fact]
    public async Task TwoMigrateRunsAtOnce_BothComplete_AndTheSchemaIsMigratedExactlyOnce()
    {
        var database = await CreateEmptyDatabaseAsync(fixture.ConnectionString);
        var gate = new RendezvousCommandInterceptor(participants: 2);
        var midChain = new MidChainRendezvousInterceptor();
        await using var first = new CroftWebDbContext(BuildOptions(database, gate, midChain));
        await using var second = new CroftWebDbContext(BuildOptions(database, gate, midChain));

        // Both runs start at once, and the gate holds each run's first command until the other
        // run's first command has arrived: the two migrate runs are guaranteed to overlap at the
        // database instead of racing on task scheduling. The mid-chain gate then forces the
        // overlap to be the colliding one (the losing run re-reads the history between the
        // winner's migrations) unless the entry serialises the runs itself. No timing guesses.
        await Task.WhenAll(MigrationRunner.RunAsync(first), MigrationRunner.RunAsync(second));

        // Both runs completed without an exception; the schema is migrated exactly once.
        var allMigrations = first.Database.GetMigrations();
        var appliedMigrations = await first.Database.GetAppliedMigrationsAsync();
        appliedMigrations.Should().Equal(allMigrations, "each migration must be applied exactly once");
        (await first.Database.GetPendingMigrationsAsync()).Should().BeEmpty();

        // On failure the stray database stays behind and goes away with the shared container,
        // the same tolerance PostgresDatabaseFixture documents.
        await DropDatabaseAsync(database);
    }

    private static DbContextOptions<CroftWebDbContext> BuildOptions(
        string connectionString, RendezvousCommandInterceptor gate, MidChainRendezvousInterceptor midChain) =>
        new DbContextOptionsBuilder<CroftWebDbContext>()
            .UseNpgsql(connectionString)
            .AddInterceptors(gate, midChain)
            .Options;

    private static async Task<string> CreateEmptyDatabaseAsync(string classConnectionString)
    {
        var databaseName = $"blog_web_migrate_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(classConnectionString);
        var admin = new NpgsqlConnectionStringBuilder(builder.ConnectionString) { Database = "postgres" };

        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
        await command.ExecuteNonQueryAsync();

        return new NpgsqlConnectionStringBuilder(builder.ConnectionString) { Database = databaseName }.ConnectionString;
    }

    private static async Task DropDatabaseAsync(string connectionString)
    {
        NpgsqlConnection.ClearPool(new NpgsqlConnection(connectionString));
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var admin = new NpgsqlConnectionStringBuilder(builder.ConnectionString) { Database = "postgres" };

        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE \"{builder.Database}\" WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
    }
}
