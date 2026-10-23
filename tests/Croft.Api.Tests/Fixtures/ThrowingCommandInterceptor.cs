using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Croft.Api.Tests.Fixtures;

/// <summary>
/// Fails every reader command with the given exception before it reaches the database, so
/// tests can pin how a caller reacts to failures and cancellations coming out of a query.
/// </summary>
internal sealed class ThrowingCommandInterceptor(Exception exception) : DbCommandInterceptor
{
    private int _executionCount;

    public int ExecutionCount => Volatile.Read(ref _executionCount);

    /// <summary>Completes as soon as a failing query has been reached.</summary>
    public TaskCompletionSource Threw { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Record();
        throw exception;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Record();
        throw exception;
    }

    private void Record()
    {
        Interlocked.Increment(ref _executionCount);
        Threw.TrySetResult();
    }
}
