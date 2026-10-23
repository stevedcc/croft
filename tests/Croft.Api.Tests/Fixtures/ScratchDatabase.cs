using Croft.Api.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Croft.Api.Tests.Fixtures;

/// <summary>
/// A migrated, empty database of its own inside the class database's server, for tests that need
/// to own every row (an export reads all of them) or want two databases (export, then import).
/// Dropped on dispose.
/// </summary>
public sealed class ScratchDatabase : IAsyncDisposable
{
    private ScratchDatabase(string connectionString) => ConnectionString = connectionString;

    public string ConnectionString { get; }

    public static async Task<ScratchDatabase> CreateAsync(string classConnectionString)
    {
        var databaseName = $"blog_scratch_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(classConnectionString);
        var admin = new NpgsqlConnectionStringBuilder(builder.ConnectionString) { Database = "postgres" };

        await using (var connection = new NpgsqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
            await command.ExecuteNonQueryAsync();
        }

        var scratch = new ScratchDatabase(new NpgsqlConnectionStringBuilder(builder.ConnectionString) { Database = databaseName }.ConnectionString);
        await using var db = scratch.CreateContext();
        await db.Database.MigrateAsync();
        return scratch;
    }

    public BlogDbContext CreateContext(string? options = null)
    {
        var connectionString = options is null
            ? ConnectionString
            : new NpgsqlConnectionStringBuilder(ConnectionString) { Options = options }.ConnectionString;
        return new BlogDbContext(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(connectionString).Options);
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearPool(new NpgsqlConnection(ConnectionString));
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString);
        var admin = new NpgsqlConnectionStringBuilder(builder.ConnectionString) { Database = "postgres" };
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE \"{builder.Database}\" WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
    }
}
