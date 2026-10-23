using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Croft.Api.Tests.Fixtures;

/// <summary>
/// Test-only gate for cache-aside straddle tests: commands run normally, but while
/// <see cref="Arm"/>ed the RESULTS of commands whose text matches <c>textMustContain</c> are
/// held (in the *Executed callbacks — the command has already run, so its snapshot predates
/// whatever happens during the hold) until <see cref="Release"/>. That pins a read's factory
/// between its query and its completion, exactly where a concurrent write's invalidation can
/// race it. Every executed command is counted so tests can tell whether a read really queried.
/// </summary>
internal sealed class HeldResultInterceptor(string textMustContain) : DbCommandInterceptor
{
    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _armed;
    private int _commands;

    /// <summary>Completes as soon as the first matching command's result is being held.</summary>
    public Task Held => _held.Task;

    /// <summary>Every command that has executed since the gate was created.</summary>
    public int Commands => Volatile.Read(ref _commands);

    public void Arm() => _armed = true;

    public void Release() => _released.TrySetResult();

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        Interlocked.Increment(ref _commands);
        return result;
    }

    public override async ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _commands);
        await HoldAsync(command);
        return result;
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        Interlocked.Increment(ref _commands);
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _commands);
        return ValueTask.FromResult(result);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        Interlocked.Increment(ref _commands);
        return result;
    }

    public override ValueTask<object?> ScalarExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, object? result,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _commands);
        return ValueTask.FromResult(result);
    }

    private async Task HoldAsync(DbCommand command)
    {
        // WORKAROUND(W-060, EF Core): keys on text inside the command EF issues
        if (!_armed || !command.CommandText.Contains(textMustContain, StringComparison.Ordinal))
            return;
        // END WORKAROUND(W-060)

        _held.TrySetResult();
        // Bounded so a gate that never releases surfaces as a failed test, not a hung run.
        await _released.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
