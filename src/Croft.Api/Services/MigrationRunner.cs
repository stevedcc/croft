using Croft.Api.Data;
using Croft.Shared;
using Microsoft.EntityFrameworkCore;

namespace Croft.Api.Services;

/// <summary>
/// Applies the pending database migrations, one migration chain at a time across API instances.
/// EF's own migration lock covers a single migration only — it is released at that migration's
/// commit while the pending list is computed once at the start of the run — so two concurrent
/// Migrate runs over a chain of two or more pending migrations both replay the same entry and
/// collide on its DDL (observed: "column ... of relation ... already exists" on the second
/// migration). Wrapping the whole chain in one session-scoped advisory lock serialises the
/// runs; the queued runner then re-reads EF's migration history and finds nothing pending.
/// </summary>
public static class MigrationRunner
{
    /// <summary>Runs the API's "migrate" entry: apply all pending migrations, waiting for a concurrent run.</summary>
    public static Task RunAsync(BlogDbContext db, CancellationToken ct = default) =>
        // WORKAROUND(W-021, EF Core): Migrate runs inside the session advisory lock; EF's own lock covers one migration only
        BackgroundJobLock.RunAsync(
            db, BackgroundJobIds.Migrations, token => db.Database.MigrateAsync(token), ct);
        // END WORKAROUND(W-021)
}
