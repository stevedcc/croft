using Croft.Api.Data;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Croft.Api.Services;

/// <summary>
/// Readiness: the primary database answers a trivial query within <see cref="Timeout"/>. It opens a
/// fresh, unpooled connection from the primary connection string (never an idle pooled one, which
/// could be stale, and never the read-replica routing: a replica answering says nothing about
/// whether writes can reach the primary). Npgsql honours a cancellation token only after the TCP
/// connect, so the bound is also the connect timeout. Unhealthy means "take no traffic";
/// liveness never asks the database (a restart cannot fix it).
/// </summary>
public sealed class DatabaseHealthCheck(IConfiguration configuration) : IHealthCheck
{
    /// <summary>The check gives up after this, inside the kubelet's probe timeout (5 s in the manifests).</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(Timeout);
        try
        {
            var builder = BlogDbConnectionString.Build(configuration);
            builder.Pooling = false;
            builder.Timeout = (int)Timeout.TotalSeconds;
            builder.CommandTimeout = (int)Timeout.TotalSeconds;
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(bound.Token);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync(bound.Token);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is NpgsqlException or OperationCanceledException or System.IO.IOException or TimeoutException
            or System.Net.Sockets.SocketException)
        {
            // The caller's own cancellation is not a verdict on the database.
            cancellationToken.ThrowIfCancellationRequested();
            return HealthCheckResult.Unhealthy($"The primary database did not answer within {Timeout.TotalSeconds:0} s: {ex.GetType().Name}");
        }
    }
}
