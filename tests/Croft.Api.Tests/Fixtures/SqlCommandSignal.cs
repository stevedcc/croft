using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Croft.Api.Tests.Fixtures;

/// <summary>
/// Counts the SQL commands that finished executing and whose text matches a predicate — after
/// execution, so a counted command has really run. Used to count scheduler ticks: every tick
/// issues its due-posts read and its earliest-scheduled read, both of which mention the Posts
/// table, so the count is a faithful lower bound on tick activity.
/// </summary>
internal sealed class SqlCommandSignal(string textMustContain) : DbCommandInterceptor
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        Counted(command);
        return result;
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        Counted(command);
        return ValueTask.FromResult(result);
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        Counted(command);
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Counted(command);
        return ValueTask.FromResult(result);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        Counted(command);
        return result;
    }

    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        Counted(command);
        return ValueTask.FromResult(result);
    }

    private void Counted(DbCommand command)
    {
        if (command.CommandText.Contains(textMustContain, StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _count);
        }
    }
}
