// WORKAROUND-FILE(W-062, xUnit): xunit's assembly fixture starts for every run, also one that needs no database, so the container is reference counted
using Testcontainers.PostgreSql;

namespace Croft.Api.Tests.Fixtures;

/// <summary>
/// The one Postgres Testcontainer shared by every test class in the assembly. xunit.v3's
/// assembly-level fixture would be created for every run, also a filtered one that needs no
/// database (docs/workarounds.md W-062), so the per-class <see cref="PostgresDatabaseFixture"/> instances share
/// the container through this reference-counted holder: the first class to run starts it and the
/// last class to finish disposes it, keeping the run deterministic (exactly one container per
/// run, gone when the last database class ends) without a collection fixture that would
/// serialise the classes. When CROFT_TEST_POSTGRES is set to a non-blank connection string
/// (Chargehand's verify lane sets it), no container is started and that server is used instead.
/// </summary>
public static class SharedPostgresContainer
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static PostgreSqlContainer? _container;
    private static int _leases;

    public static async Task<string> AcquireAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            // A given server replaces the container; the lease counting is unchanged, so
            // ReleaseAsync pairs it and disposes nothing (no container was started).
            var givenServer = TestPostgres.GivenServer(Environment.GetEnvironmentVariable("CROFT_TEST_POSTGRES"));
            if (givenServer is not null)
            {
                _leases++;
                return givenServer;
            }

            if (_container is null)
            {
                var container = new PostgreSqlBuilder("postgres:18-alpine")
                    .WithDatabase("blog")
                    .WithUsername("blog")
                    .WithPassword("blog")
                    .Build();
                // Publish the started container only after StartAsync succeeds, so a failed
                // start leaves no lease behind for the next class to trip over.
                await container.StartAsync(cancellationToken);
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
