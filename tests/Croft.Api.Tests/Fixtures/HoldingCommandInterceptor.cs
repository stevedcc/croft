using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Croft.Api.Tests.Fixtures;

/// <summary>
/// Test-only gate: once <see cref="Arm"/>ed, holds every database command until
/// <see cref="Release"/>, so concurrent requests stay pinned on open connections while the test
/// looks at the server. <see cref="Held"/> completes when <c>holdCount</c> commands have arrived
/// and are being held. Commands that arrive after the release pass straight through.
/// </summary>
internal sealed class HoldingCommandInterceptor(int holdCount) : DbCommandInterceptor
{
    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _armed;
    private int _arrived;

    /// <summary>Commands that have arrived at the gate so far.</summary>
    public int Arrived => Volatile.Read(ref _arrived);

    /// <summary>Completes as soon as <c>holdCount</c> commands are being held.</summary>
    public Task Held => _held.Task;

    public void Arm() => _armed = true;

    public void Release() => _released.TrySetResult();

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        await HoldAsync();
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await HoldAsync();
        return result;
    }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        await HoldAsync();
        return result;
    }

    private async Task HoldAsync()
    {
        if (!_armed)
            return;

        if (Interlocked.Increment(ref _arrived) >= holdCount)
            _held.TrySetResult();

        // Bounded so a gate that never releases surfaces as a failed test, not a hung run.
        await _released.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
