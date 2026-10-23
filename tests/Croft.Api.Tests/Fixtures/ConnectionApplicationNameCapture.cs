using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Croft.Api.Tests.Fixtures;

/// <summary>
/// Records the Application Name of the connection every executed command ran on, from the
/// command's own connection: <c>croft-api-read</c> means the read replica, anything else is
/// the primary's name. Tests use it to see which database a request really touched.
/// </summary>
internal sealed class ConnectionApplicationNameCapture : DbCommandInterceptor
{
    private readonly ConcurrentQueue<string> _names = new();

    public IReadOnlyList<string> Names => [.. _names];

    public void Clear() => _names.Clear();

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        Record(command);
        return result;
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        Record(command);
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        Record(command);
        return result;
    }

    public override ValueTask<object?> ScalarExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, object? result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    private void Record(DbCommand command)
    {
        var name = new NpgsqlConnectionStringBuilder(command.Connection!.ConnectionString).ApplicationName ?? string.Empty;
        _names.Enqueue(name);
    }
}
