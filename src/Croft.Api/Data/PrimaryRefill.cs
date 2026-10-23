using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Croft.Api.Data;

/// <summary>
/// Marks the code running a public-read cache refill that must read from the primary (brief 13c
/// item 2): the cache refills an entry its group invalidated a moment ago, and a replica may not
/// have replayed the write that caused the invalidation yet. The marker is ambient (an
/// <see cref="AsyncLocal{T}"/>), so the cache needs no knowledge of the DbContext and the services
/// need no extra parameter; <see cref="PrimaryRefillConnectionInterceptor"/> acts on it when the
/// refill's connection is about to open.
/// </summary>
public static class PrimaryRefill
{
    private static readonly AsyncLocal<bool> Marked = new();

    /// <summary>Whether the code running now is a refill that must read from the primary.</summary>
    public static bool IsActive => Marked.Value;

    /// <summary>Marks the code running until the returned scope is disposed.</summary>
    public static IDisposable Begin()
    {
        var previous = Marked.Value;
        Marked.Value = true;
        return new Scope(previous);
    }

    private sealed class Scope(bool previous) : IDisposable
    {
        public void Dispose() => Marked.Value = previous;
    }
}

/// <summary>
/// Points a connection that is about to open at the primary when a <see cref="PrimaryRefill"/> is
/// running. A request routed to the read replica (<see cref="BlogDbConnectionString.ForRequest"/>)
/// built its DbContext before the cache knew whether the refill must read the primary; this is where
/// the decision reaches the connection.
/// </summary>
public sealed class PrimaryRefillConnectionInterceptor(IConfiguration configuration) : DbConnectionInterceptor
{
    public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        UsePrimary(connection);
        return result;
    }

    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        UsePrimary(connection);
        return ValueTask.FromResult(result);
    }

    private void UsePrimary(DbConnection connection)
    {
        if (!PrimaryRefill.IsActive || connection.State != System.Data.ConnectionState.Closed)
            return;

        var primary = BlogDbConnectionString.Build(configuration).ConnectionString;
        if (connection.ConnectionString != primary)
            connection.ConnectionString = primary;
    }
}
