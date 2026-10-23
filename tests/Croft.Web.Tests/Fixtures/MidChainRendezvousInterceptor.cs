// WORKAROUND-FILE(W-060, EF Core): keys on the LOCK TABLE, INSERT and history-read statements EF and Npgsql issue during a migration
using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Croft.Web.Tests.Fixtures;

/// <summary>
/// Makes two concurrent migrate runs overlap mid-chain deterministically (the missing half of
/// <see cref="RendezvousCommandInterceptor"/>'s first-command rendezvous). EF Core commits each
/// migration in its own transaction and Npgsql's migration lock is transaction-scoped, so a
/// losing run re-reads the migration history whenever it wakes — between the winner's migrations
/// or after them, as scheduling decides. Only the former collides, so this gate forces exactly
/// that shape in the failing direction:
/// the winner's first history INSERT is held until the losing run's <c>LOCK TABLE</c> has been
/// sent — the loser then queues for the history-table lock before the winner's first commit and
/// wins it at that commit — and the winner's later <c>LOCK TABLE</c>s are held until the loser has
/// re-read the history mid-chain (proving it holds the lock and computed "one migration applied,
/// the rest pending"). Both runs then apply the same second migration and collide. When the
/// migrate entry serialises runs itself (its <c>pg_advisory</c> acquire is observed) there is no
/// concurrent chain to force and nothing is held. Both holds are bounded (30 s) so a run that
/// never reaches the chain fails the test instead of hanging it.
/// </summary>
internal sealed class MidChainRendezvousInterceptor : DbCommandInterceptor
{
    private readonly ConcurrentDictionary<DbContext, ContextState> _contexts = new();
    private readonly TaskCompletionSource _racingRunIsInTheChain = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _racingRunReReadsMidChain = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _historyInserts;
    private DbContext? _firstInsertContext;

    private sealed class ContextState
    {
        public int LockTables;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        var text = command.CommandText;
        if (text.Contains("pg_advisory", StringComparison.Ordinal))
        {
            // The entry serialises its runs before the chain: nothing to force, release everything.
            _racingRunIsInTheChain.TrySetResult();
            _racingRunReReadsMidChain.TrySetResult();
        }

        if (StateOf(eventData.Context) is not { } state)
        {
            return result;
        }

        if (text.Contains("LOCK TABLE", StringComparison.Ordinal))
        {
            state.LockTables++;
            if (state.LockTables == 1
                && _firstInsertContext is not null
                && !ReferenceEquals(_firstInsertContext, eventData.Context))
            {
                // Another run is entering the chain while the winner's first history insert is
                // held: its request reaches the server first and queues for the history-table
                // lock behind the winner's still-open transaction.
                _racingRunIsInTheChain.TrySetResult();
            }
            else if (state.LockTables > 1)
            {
                await HoldAsync(_racingRunReReadsMidChain.Task, cancellationToken);
            }
        }

        if (text.Contains("INSERT INTO \"__EFMigrationsHistory\"", StringComparison.Ordinal)
            && Interlocked.Increment(ref _historyInserts) == 1)
        {
            _firstInsertContext = eventData.Context;
            if (_contexts.Keys.Any(context =>
                    !ReferenceEquals(context, eventData.Context)
                    && _contexts[context].LockTables > 0))
            {
                // The other order: the racing run's LOCK TABLE already went out.
                _racingRunIsInTheChain.TrySetResult();
            }
            await HoldAsync(_racingRunIsInTheChain.Task, cancellationToken);
        }

        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        var text = command.CommandText;
        if (StateOf(eventData.Context) is { LockTables: > 0 }
            && text.Contains("FROM \"__EFMigrationsHistory\"", StringComparison.Ordinal)
            && _firstInsertContext is not null
            && !ReferenceEquals(_firstInsertContext, eventData.Context))
        {
            // The racing run holds the history-table lock and is computing its pending list
            // mid-chain. Its own pre-chain reads never reach here (no LOCK TABLE yet), and the
            // winner's post-lock reads are excluded by context.
            _racingRunReReadsMidChain.TrySetResult();
        }
        return ValueTask.FromResult(result);
    }

    private ContextState? StateOf(DbContext? context) =>
        context is null ? null : _contexts.GetOrAdd(context, _ => new ContextState());

    private static async Task HoldAsync(Task until, CancellationToken cancellationToken)
    {
        // Bounded so a participant that never reaches the chain surfaces as a failed test,
        // not a hung run.
        await until.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
    }
}
