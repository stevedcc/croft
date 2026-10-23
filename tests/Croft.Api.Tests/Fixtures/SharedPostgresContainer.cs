// WORKAROUND-FILE(W-062, xUnit): xUnit v2 has no assembly-level fixture, so the container is reference counted
using Testcontainers.PostgreSql;

namespace Croft.Api.Tests.Fixtures;

/// <summary>
/// The one Postgres Testcontainer shared by every test class in the assembly. xUnit v2 has no
/// assembly-level fixture, so the per-class <see cref="PostgresDatabaseFixture"/> instances share
/// the container through this reference-counted holder: the first class to run starts it and the
/// last class to finish disposes it, keeping the run deterministic (exactly one container per
/// run, gone when the last database class ends) without a collection fixture that would
/// serialise the classes.
/// </summary>
public static class SharedPostgresContainer
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static PostgreSqlContainer? _container;
    private static int _leases;

    public static async Task<string> AcquireAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (_container is null)
            {
                var container = new PostgreSqlBuilder("postgres:18-alpine")
                    .WithDatabase("blog")
                    .WithUsername("blog")
                    .WithPassword("blog")
                    .Build();
                // Publish the started container only after StartAsync succeeds, so a failed
                // start leaves no lease behind for the next class to trip over.
                await container.StartAsync();
                _container = container;
            }

            _leases++;
            return _container.GetConnectionString();
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task ReleaseAsync()
    {
        PostgreSqlContainer? expired = null;
        await Gate.WaitAsync();
        try
        {
            if (_leases > 0 && --_leases == 0)
            {
                expired = _container;
                _container = null;
            }
        }
        finally
        {
            Gate.Release();
        }

        if (expired is not null)
        {
            // Disposing the container removes it and every class database still inside it.
            await expired.DisposeAsync();
        }
    }
}
