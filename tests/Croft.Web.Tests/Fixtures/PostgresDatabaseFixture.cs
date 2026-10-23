using Croft.Web.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Croft.Web.Tests.Fixtures;

/// <summary>
/// One database per test class, created from the Web migrations when the class starts and
/// dropped when it finishes. Classes run in parallel, each on its own database inside the one
/// shared Testcontainer. Tests inside a class still run serially and see each other's rows, so
/// they must scope their assertions to data they created themselves. Mirrors the API suite's
/// fixture of the same name, over <see cref="CroftWebDbContext"/>.
/// </summary>
public sealed class PostgresDatabaseFixture : IAsyncLifetime
{
    private string? _connectionString;
    private bool _leased;

    public string ConnectionString => _connectionString
        ?? throw new InvalidOperationException("The class database has not been created.");

    public async Task InitializeAsync()
    {
        var containerConnectionString = await SharedPostgresContainer.AcquireAsync();
        _leased = true;
        try
        {
            _connectionString = await CreateDatabaseAsync(containerConnectionString);

            var options = new DbContextOptionsBuilder<CroftWebDbContext>()
                .UseNpgsql(_connectionString)
                .Options;
            await using var db = new CroftWebDbContext(options);
            await db.Database.MigrateAsync();
        }
        catch
        {
            // A class whose setup failed must not keep the container alive: undo the lease and
            // drop whatever was created before rethrowing. Half-migrated databases cannot be
            // dropped here reliably (the migrations connection may be the failure), and if the
            // drop fails too, TeardownAsync still releases the lease in its finally block;
            // only the stray database remains, and it goes away with the container.
            await TeardownAsync();
            throw;
        }
    }

    public Task DisposeAsync() => TeardownAsync();

    private async Task TeardownAsync()
    {
        try
        {
            if (_connectionString is not null)
            {
                // Close this database's idle pooled connections first — scoped to this pool only,
                // because parallel classes have live pools of their own that ClearAllPools would
                // destroy — then force-drop: WITH (FORCE) also terminates any connection a leaked
                // factory could still hold.
                NpgsqlConnection.ClearPool(new NpgsqlConnection(_connectionString));

                var builder = new NpgsqlConnectionStringBuilder(_connectionString);
                var admin = new NpgsqlConnectionStringBuilder(builder.ConnectionString)
                {
                    Database = "postgres"
                };
                await using var connection = new NpgsqlConnection(admin.ConnectionString);
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = $"DROP DATABASE \"{builder.Database}\" WITH (FORCE)";
                await command.ExecuteNonQueryAsync();

                _connectionString = null;
            }
        }
        finally
        {
            // The lease is released even when the drop above threw (ClearPool, the admin
            // connection or the DROP itself): otherwise the shared container's refcount
            // never reaches zero and the container leaks for the rest of the run.
            if (_leased)
            {
                _leased = false;
                await SharedPostgresContainer.ReleaseAsync();
            }
        }
    }

    private static async Task<string> CreateDatabaseAsync(string containerConnectionString)
    {
        var databaseName = $"blog_web_class_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(containerConnectionString);
        var admin = new NpgsqlConnectionStringBuilder(builder.ConnectionString)
        {
            Database = "postgres"
        };

        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
        await command.ExecuteNonQueryAsync();

        return new NpgsqlConnectionStringBuilder(builder.ConnectionString)
        {
            Database = databaseName
        }.ConnectionString;
    }
}
