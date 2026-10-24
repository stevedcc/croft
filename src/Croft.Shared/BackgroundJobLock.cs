using Microsoft.EntityFrameworkCore;

namespace Croft.Shared;

/// <summary>
/// Runs work while holding the job's Postgres advisory lock, so that when several instances
/// reach the same job at the same time exactly one does the work. Two acquire styles, one
/// mechanism (advisory locks, one stable id per job in the caller's job-id constants):
/// <see cref="TryRunAsync"/> skips when the lock is held (background job ticks), and
/// <see cref="RunAsync"/> waits for it (the migrate entries, where every caller must see the
/// result and none may skip).
/// </summary>
public static class BackgroundJobLock
{
    /// <summary>
    /// Runs <paramref name="work"/> inside a transaction holding the job's advisory lock
    /// (<c>pg_try_advisory_xact_lock</c>). Returns false without running the work when another
    /// instance already holds the lock. The lock is transaction-scoped: it is released when the
    /// tick's transaction ends, including when an instance dies mid-tick.
    /// </summary>
    public static async Task<bool> TryRunAsync(
        DbContext db, long jobId, Func<CancellationToken, Task> work, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // pg_try_advisory_xact_lock never blocks: an instance either gets the lock or skips the
        // tick right away. The probe runs on the tick's own transaction (EF enlists it).
        if (!await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({jobId}) AS \"Value\"").SingleAsync(ct))
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        await work(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    // WORKAROUND(W-021, EF Core): EF's migration lock is released at each migration commit, so one session advisory lock wraps the whole chain
    /// <summary>
    /// Runs <paramref name="work"/> while holding the job's SESSION-level advisory lock
    /// (<c>pg_advisory_lock</c>/<c>pg_advisory_unlock</c>), waiting for a holder instead of
    /// skipping. The session scope is deliberate: Migrate manages its own per-migration
    /// transactions and EF warns (MigrationsUserTransactionWarning) that wrapping it in an
    /// outside transaction both disables its migration lock and poisons its error-tolerant
    /// bootstrap. The lock dies with the connection when the process crashes mid-work. A
    /// cancellation that aborts the unlock itself could return the still-locked connection to
    /// a pool (session locks outlive pool reuse), so callers that honour a token should pass
    /// none to this method; the migrate entries do.
    /// </summary>
    public static async Task RunAsync(
        DbContext db, long jobId, Func<CancellationToken, Task> work, CancellationToken ct = default)
    {
        // The connection is pinned open so the lock grant and the work share one session.
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await AcquireAsync(db, jobId, ct);
            try
            {
                await work(ct);
            }
            finally
            {
                // Released here even when the work failed. A connection that died takes the
                // session lock with it; a failed unlock on a live connection surfaces as the
                // migrate entry's failure instead of a silently leaked lock.
                await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_unlock({jobId})", ct);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
    // END WORKAROUND(W-021)

    // WORKAROUND(W-021, Npgsql): the default 30 s command timeout would fail a queued migrator waiting for the lock, so it is lifted around the acquire
    private static async Task AcquireAsync(DbContext db, long jobId, CancellationToken ct)
    {
        // The WAIT for the lock must be unbounded: a queued migrator waits out the holding pod's
        // whole migration chain, and under the default 30 s command timeout the wait itself fails
        // the init container (Kubernetes restarts it with back-off) without ever having run
        // anything. The timeout is lifted around the acquire only and restored before the work,
        // which runs under its normal command timeouts. The acquire stays a plain EF command so
        // command interceptors see it like every other.
        db.Database.SetCommandTimeout(0);
        try
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_lock({jobId})", ct);
        }
        finally
        {
            db.Database.SetCommandTimeout(null);
        }
    }
    // END WORKAROUND(W-021)
}
