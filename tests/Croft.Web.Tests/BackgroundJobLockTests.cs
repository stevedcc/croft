using Croft.Shared;
using Croft.Web.Data;
using Croft.Web.Tests.Fixtures;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Croft.Web.Tests;

/// <summary>
/// The migrate entries' lock wait must be unbounded: a queued migrator (a Web pod's init
/// container behind another pod's) waits however long the holder's migration chain takes and
/// only then does its own work under normal command timeouts. Under the default 30 s command
/// timeout the WAIT for the lock fails first — the init container exits non-zero and Kubernetes
/// restarts it with back-off — which is exactly the failure this pins.
/// </summary>
[Trait("Category", "Container")]
public class BackgroundJobLockTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    [Fact]
    public async Task RunAsync_LockHeldLongerThanTheCommandTimeout_WaiterWaitsForItAndOnlyThenRunsItsWork()
    {
        const long jobId = 42;

        // The waiter's command timeout is 2 s and the holder keeps the lock for 3 s: the hold outlasts
        // the timeout, which is the premise (an unbounded lock wait past the command timeout), at a
        // tenth of the cost of the 35 s hold past the default 30 s this test used to wait out.
        var waiterConnection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { CommandTimeout = 2 }.ConnectionString;
        await using var db = new CroftWebDbContext(
            new DbContextOptionsBuilder<CroftWebDbContext>().UseNpgsql(waiterConnection).Options);

        await using var holder = new NpgsqlConnection(fixture.ConnectionString);
        await holder.OpenAsync();
        await ExecuteAsync(holder, $"SELECT pg_advisory_lock({jobId})");
        // The holder's sleep and its release are one session's work, so the waiter cannot observe a gap.
        var hold = ExecuteAsync(holder, "SELECT pg_sleep(3)");

        var workRan = false;
        var waiter = Task.Run(() => BackgroundJobLock.RunAsync(db, jobId, _ =>
        {
            workRan = true;
            return Task.CompletedTask;
        }));

        // The waiter is really queued on the lock (a concrete condition, read from the server), so the
        // check that its work has not run is a check on a waiter that is waiting, not on one that has
        // not started. If it never takes the lock at all (RunAsync skipped pg_advisory_lock) the work
        // would run at once and this fails.
        await WaitUntilAWaiterQueuesAsync();
        workRan.Should().BeFalse("the work must not run while another session holds the lock");

        await hold;
        await ExecuteAsync(holder, $"SELECT pg_advisory_unlock({jobId})");

        // Bounded so a waiter that never acquires surfaces as a failed test, not a hung run.
        await waiter.WaitAsync(TimeSpan.FromSeconds(30));

        workRan.Should().BeTrue("the waiter runs its work only after the holder released the lock");
    }

    /// <summary>Waits (bounded, polling the server) until a session is queued on an advisory lock of this database.</summary>
    private async Task WaitUntilAWaiterQueuesAsync()
    {
        await using var probe = new NpgsqlConnection(fixture.ConnectionString);
        await probe.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            await using var command = probe.CreateCommand();
            // The server's own account of who waits on what: a session of this database blocked on an advisory lock.
            command.CommandText = "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND wait_event = 'advisory'";
            if ((long)(await command.ExecuteScalarAsync())! > 0)
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("no session queued on the advisory lock within 30 s: the waiter never asked for it");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        // The holder's sleep is longer than the waiter's command timeout, and that hold is deliberate here.
        command.CommandTimeout = 0;
        await command.ExecuteScalarAsync();
    }
}
