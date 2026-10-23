using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;

namespace Croft.Shared;

/// <summary>
/// How both apps shed load and bound slow clients. The settings live under
/// <c>Croft:&lt;App&gt;:</c> (<c>Croft:Web:</c>, <c>Croft:Api:</c>); every key has a default.
/// <list type="bullet">
/// <item><c>MaxConcurrentRequests</c> (64) and <c>RequestQueueLimit</c> (64): requests in progress
/// and waiting across all clients, for everything except media.</item>
/// <item><c>MaxConcurrentMediaRequests</c> (32): the separate partition for <c>/api/media</c>, so
/// images can never starve pages and pages never starve images; it queues like the pages do.</item>
/// <item><c>MaxConcurrentRequestsPerClient</c> (Web 16, API 32) and
/// <c>MaxConcurrentMediaRequestsPerClient</c> (Web 16, API 32): requests in progress per client,
/// pages and media counted in separate per-client partitions (a page's images never compete with
/// the page itself for the client's permits). Beyond its permits a client queues, oldest first, up
/// to <c>ClientQueueLimit</c> (32) per partition; beyond that it is refused at once. The per-client
/// stage comes first in the chain, so a client's queued requests hold no global permit and one
/// client holding everything it may never blocks another.</item>
/// <item><c>MinResponseDataRate</c> and <c>MinRequestBodyDataRate</c> (2048 bytes per second) with
/// <c>MinDataRateGracePeriodSeconds</c> (10): a connection reading or sending slower is aborted.</item>
/// <item><c>MaxConcurrentConnections</c> (512).</item>
/// </list>
/// The limits are read when the limiter creates a partition (the first request), so they come
/// from the host's full configuration however it was supplied.
/// </summary>
public static class RequestLimits
{
    private const int DefaultMaxConcurrentRequests = 64;
    private const int DefaultRequestQueueLimit = 64;
    private const int DefaultMaxConcurrentMediaRequests = 32;
    private const int DefaultClientQueueLimit = 32;
    private const int DefaultMinDataRate = 2048;
    private const double DefaultGracePeriodSeconds = 10;
    private const int DefaultMaxConcurrentConnections = 512;

    /// <summary>
    /// The chained limiter. <paramref name="exempt"/> requests are never limited;
    /// <paramref name="isMedia"/> requests count against the media partition instead of the
    /// pages one; <paramref name="clientKey"/> names the client a request counts against, or is
    /// null when it has none (then only the global limiter applies).
    /// </summary>
    public static PartitionedRateLimiter<HttpContext> CreateLimiter(
        IConfiguration configuration,
        string app,
        int defaultPerClient,
        int defaultMediaPerClient,
        Func<HttpContext, bool> exempt,
        Func<HttpContext, bool> isMedia,
        Func<HttpContext, string?> clientKey)
    {
        var prefix = $"Croft:{app}:";

        var global = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            if (exempt(context))
                return RateLimitPartition.GetNoLimiter("exempt");

            var media = isMedia(context);
            return RateLimitPartition.GetConcurrencyLimiter(media ? "media" : "requests", _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = media
                    ? configuration.GetValue(prefix + "MaxConcurrentMediaRequests", DefaultMaxConcurrentMediaRequests)
                    : configuration.GetValue(prefix + "MaxConcurrentRequests", DefaultMaxConcurrentRequests),
                QueueLimit = configuration.GetValue(prefix + "RequestQueueLimit", DefaultRequestQueueLimit),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            });
        });

        var perClient = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            if (exempt(context) || clientKey(context) is not { } client)
                return RateLimitPartition.GetNoLimiter("exempt");

            var media = isMedia(context);
            return RateLimitPartition.GetConcurrencyLimiter((media ? "media|" : "pages|") + client, _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = media
                    ? configuration.GetValue(prefix + "MaxConcurrentMediaRequestsPerClient", defaultMediaPerClient)
                    : configuration.GetValue(prefix + "MaxConcurrentRequestsPerClient", defaultPerClient),
                QueueLimit = configuration.GetValue(prefix + "ClientQueueLimit", DefaultClientQueueLimit),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            });
        });

        // Per client first: a request waiting for its client's permits holds no global one.
        return PartitionedRateLimiter.CreateChained(perClient, global);
    }

    /// <summary>
    /// The key of a client address: IPv4 as it is, IPv6 by its /64 (one subscriber holds a whole
    /// /64, so a per-address key would be free to rotate), null for no address.
    /// </summary>
    public static string? ClientKey(IPAddress? address)
    {
        if (address is null)
            return null;

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return address.ToString();

        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        bytes[8..].Clear();
        return new IPAddress(bytes).ToString() + "/64";
    }

    /// <summary>Kestrel's minimum data rates and connection ceiling from configuration.</summary>
    public static void ConfigureKestrel(KestrelServerOptions server, IConfiguration configuration, string app)
    {
        var prefix = $"Croft:{app}:";
        var grace = TimeSpan.FromSeconds(configuration.GetValue(prefix + "MinDataRateGracePeriodSeconds", DefaultGracePeriodSeconds));
        server.Limits.MinResponseDataRate = new MinDataRate(
            configuration.GetValue(prefix + "MinResponseDataRate", DefaultMinDataRate), grace);
        server.Limits.MinRequestBodyDataRate = new MinDataRate(
            configuration.GetValue(prefix + "MinRequestBodyDataRate", DefaultMinDataRate), grace);
        server.Limits.MaxConcurrentConnections = configuration.GetValue(prefix + "MaxConcurrentConnections", DefaultMaxConcurrentConnections);
    }
}
