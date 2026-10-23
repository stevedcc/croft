using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Croft.Api.Tests.Fixtures;

/// <summary>
/// Test-only gate: holds every database command until all participants (distinct DbContexts) have
/// issued their first one, so concurrent runs are guaranteed to overlap at the database instead of
/// racing on task scheduling. Once the rendezvous completes, later commands pass straight through.
/// </summary>
internal sealed class RendezvousCommandInterceptor(int participants) : DbCommandInterceptor
{
    private readonly ConcurrentDictionary<DbContext, bool> _arrived = new();
    private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        await RendezvousAsync(eventData);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await RendezvousAsync(eventData);
        return result;
    }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        await RendezvousAsync(eventData);
        return result;
    }

    private async Task RendezvousAsync(CommandEventData eventData)
    {
        // The participant whose TryAdd lands last reads the count after both adds are in and
        // opens the gate; every other arrival only waits.
        if (eventData.Context is { } context
            && _arrived.TryAdd(context, true)
            && _arrived.Count >= participants)
        {
            _allArrived.TrySetResult();
        }

        // Bounded so a participant that never issues a command surfaces as a failed test,
        // not a hung run.
        await _allArrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
