using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Croft.Api.Tests.Fixtures;

/// <summary>
/// Test-only gate: holds every UPDATE a context issues until a condition the test controls has
/// been met, so a test can line up two job ticks or slip another writer's commit in between a
/// job's read and its write on concrete events instead of timing guesses. <see cref="Arrived"/>
/// completes when the first held command turns up, which for these jobs means the tick has
/// already read the rows it is about to write.
/// </summary>
internal sealed class HeldCommandInterceptor : DbCommandInterceptor
{
    private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Func<Task> _holdUntil = static () => Task.CompletedTask;

    /// <summary>Completes as soon as the first held command has been reached.</summary>
    public Task Arrived => _arrived.Task;

    /// <summary>Every held command waits for this condition before executing. Must be set before the run starts.</summary>
    public void HoldUntil(Func<Task> condition) => _holdUntil = condition;

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        await HoldAsync(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await HoldAsync(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        await HoldAsync(command);
        return result;
    }

    private async Task HoldAsync(DbCommand command)
    {
        // WORKAROUND(W-060, EF Core): keys on the command text starting with UPDATE
        // Matched against the statement start: a Contains("UPDATE") would also hit the
        // "UpdatedAt" column in the SELECTs that must run before the hold.
        if (!command.CommandText.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
            return;
        // END WORKAROUND(W-060)

        _arrived.TrySetResult();
        // Bounded so a gate that never releases surfaces as a failed test, not a hung run.
        await _holdUntil().WaitAsync(TimeSpan.FromSeconds(30));
    }
}
