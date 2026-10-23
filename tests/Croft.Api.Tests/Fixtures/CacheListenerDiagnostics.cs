using System.Text;
using Croft.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Croft.Api.Tests.Fixtures;

/// <summary>
/// Makes a "the cache listener never connected" failure self-explaining. The service keeps only the
/// outermost exception's one-line message (<see cref="CacheListenerService.LastError"/>), which
/// for Npgsql's "The connection was previously broken because of the following exception" hides
/// the exception that broke it. The service also logs every failure with the exception object, so a
/// test host that collects logs can print the whole chain, one line per exception, for every
/// failed attempt.
/// </summary>
internal static class CacheListenerDiagnostics
{
    /// <summary>Collects the host's logs so <see cref="Describe"/> can read the listener's failures.</summary>
    public static void Collect(IServiceCollection services) =>
        services.AddLogging(logging => logging.AddProvider(new LogCollector()));

    /// <summary>
    /// What the database says about the listener's backends on this database right now (state, what it
    /// waits on, who blocks it, the locks it holds or wants), on a connection of its own. A listener
    /// that never finishes its setup is either not reaching the server or waiting inside it; this tells
    /// which. Never throws: a probe that cannot run says so.
    /// </summary>
    public static async Task<string> SampleServerAsync(string connectionString)
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false, ApplicationName = "croft-test-probe", Timeout = 5 };
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT a.pid, a.state, a.wait_event_type, a.wait_event,
                       pg_blocking_pids(a.pid)::text AS blocked_by,
                       round(extract(epoch FROM now() - a.state_change)::numeric, 1) AS in_state_s,
                       left(a.query, 60) AS query,
                       coalesce((SELECT string_agg(l.locktype || '/' || l.mode || CASE WHEN l.granted THEN '' ELSE '(waiting)' END, ', ')
                                 FROM pg_locks l WHERE l.pid = a.pid), '') AS locks
                FROM pg_stat_activity a
                WHERE a.datname = current_database() AND a.application_name = 'croft-cache-listener'
                """;
            var rows = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rows.Add($"pid {reader["pid"]} {reader["state"]} wait {reader["wait_event_type"]}/{reader["wait_event"]} "
                    + $"blocked by {reader["blocked_by"]} for {reader["in_state_s"]} s, query '{reader["query"]}', locks [{reader["locks"]}]");
            }

            return rows.Count == 0 ? "server: no croft-cache-listener backend on this database" : "server: " + string.Join("; ", rows);
        }
        catch (Exception ex)
        {
            return $"server probe failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>The listener's state and every failed attempt it logged, with the full exception chain.</summary>
    public static string Describe(BlogApiFactory factory)
    {
        var listener = factory.Services.GetRequiredService<CacheListenerService>();
        var text = new StringBuilder();
        text.Append("At ").Append(DateTimeOffset.UtcNow.ToString("HH:mm:ss.fff")).Append(". ");
        // A listener that never gets its replies is either not reaching the server or not being scheduled
        // here: the pool's backlog tells which (a deep queue under all-blocked workers is starvation).
        ThreadPool.GetAvailableThreads(out var freeWorkers, out _);
        ThreadPool.GetMaxThreads(out var maxWorkers, out _);
        text.Append("Thread pool: ").Append(ThreadPool.ThreadCount).Append(" threads (").Append(maxWorkers - freeWorkers)
            .Append(" busy), ").Append(ThreadPool.PendingWorkItemCount).Append(" queued items, ")
            .Append(Environment.ProcessorCount).Append(" cores, load ").Append(File.Exists("/proc/loadavg") ? File.ReadAllText("/proc/loadavg").Split(' ')[0] : "n/a").Append(". ");
        text.Append("Listener: backend pid ").Append(listener.BackendPid?.ToString() ?? "none")
            .Append(", last error ").Append(listener.LastError ?? "none (still connecting)").Append('.');

        var collector = factory.Services.GetServices<ILoggerProvider>().OfType<LogCollector>().FirstOrDefault();
        if (collector is null)
        {
            return text.Append(" (no log collector registered: pass the factory through CacheListenerDiagnostics.Collect)").ToString();
        }

        var failures = collector.Entries
            .Where(e => e.Exception is not null && e.Category.EndsWith(nameof(CacheListenerService), StringComparison.Ordinal))
            .ToList();
        text.Append(" Failed attempts logged: ").Append(failures.Count).Append('.');
        foreach (var (entry, index) in failures.Select((e, i) => (e, i + 1)))
        {
            text.AppendLine().Append("  attempt ").Append(index).Append(" logged at ")
                .Append(entry.At.ToString("HH:mm:ss.fff")).Append(": ").Append(entry.Message);
            var depth = 0;
            for (var ex = entry.Exception; ex is not null; ex = ex.InnerException, depth++)
            {
                text.AppendLine().Append("    ").Append(string.Concat(Enumerable.Repeat("-> ", depth)))
                    .Append(ex.GetType().FullName).Append(": ").Append(ex.Message.ReplaceLineEndings(" "));
                if (ex is PostgresException pg)
                {
                    text.Append(" [SqlState ").Append(pg.SqlState).Append(']');
                }
                else if (ex is NpgsqlException { SqlState: { } state })
                {
                    text.Append(" [SqlState ").Append(state).Append(']');
                }
            }

            // Where the innermost exception was thrown from (which await of the listener's loop).
            var innermost = entry.Exception!;
            while (innermost.InnerException is not null)
            {
                innermost = innermost.InnerException;
            }

            foreach (var frame in (innermost.StackTrace ?? string.Empty).Split('\n').Take(12))
            {
                text.AppendLine().Append("      ").Append(frame.Trim());
            }
        }

        return text.ToString();
    }
}
