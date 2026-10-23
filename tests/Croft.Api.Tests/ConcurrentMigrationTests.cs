using Croft.Api.Data;
using Croft.Api.Services;
using Croft.Api.Tests.Fixtures;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Croft.Api.Tests;

/// <summary>
/// Two migrate runs against one empty database at once — two API instances starting together, or
/// an init container racing an instance. Both must complete and the schema must end up migrated
/// exactly once. This is what the migrate entry's advisory lock buys: EF's own migration lock
/// covers a single migration only (released at its commit, pending list computed once at the
/// start), so raw concurrent Migrate runs over a chain collide on the second migration's DDL
/// (observed: 42701 "column ... already exists").
/// </summary>
public class ConcurrentMigrationTests(PostgresDatabaseFixture fixture)
    : IClassFixture<PostgresDatabaseFixture>
{
    [Fact]
    public async Task TwoMigrateRunsAtOnce_BothComplete_AndTheSchemaIsMigratedExactlyOnce()
    {
        var database = await CreateEmptyDatabaseAsync(fixture.ConnectionString);
        var gate = new RendezvousCommandInterceptor(participants: 2);
        await using var first = new BlogDbContext(BuildOptions(database, gate));
        await using var second = new BlogDbContext(BuildOptions(database, gate));

        // Both runs start at once, and the gate holds each run's first command until the other
        // run's first command has arrived: the two migrate runs are guaranteed to overlap at the
        // database instead of racing on task scheduling. No timing guesses.
        await Task.WhenAll(MigrationRunner.RunAsync(first), MigrationRunner.RunAsync(second));

        // Both runs completed without an exception; the schema is migrated exactly once.
        var allMigrations = first.Database.GetMigrations();
        var appliedMigrations = await first.Database.GetAppliedMigrationsAsync();
        appliedMigrations.Should().Equal(allMigrations, "each migration must be applied exactly once");
        (await first.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        (await first.Tags.CountAsync()).Should().Be(0, "the migrated schema must be queryable");

        // On failure the stray database stays behind and goes away with the shared container,
        // the same tolerance PostgresDatabaseFixture documents.
        await DropDatabaseAsync(database);
    }

    private static DbContextOptions<BlogDbContext> BuildOptions(
        string connectionString, RendezvousCommandInterceptor gate) =>
        new DbContextOptionsBuilder<BlogDbContext>()
            .UseNpgsql(connectionString)
            .AddInterceptors(gate)
            .Options;

    private static async Task<string> CreateEmptyDatabaseAsync(string classConnectionString)
    {
        var databaseName = $"blog_migrate_{Guid.NewGuid():N}";
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
