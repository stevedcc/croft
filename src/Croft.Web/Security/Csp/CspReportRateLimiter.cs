using Microsoft.Extensions.Options;

namespace Croft.Web.Security.Csp;

/// <summary>
/// A small in-memory sliding-window rate limit for /csp-report, per pod (each pod counts for itself;
/// a shared store would only add failure modes). Three limits, all per minute:
/// <list type="bullet">
/// <item>per client key (<c>RequestLimits.ClientKey</c>: an IPv4 address or an IPv6 /64, so rotating
/// addresses inside a /64 buys nothing): <c>Csp:ReportRateLimitPerMinute</c>;</item>
/// <item>the number of client buckets held at once: <c>Csp:ReportMaxClients</c>. When the table is
/// full a new client is refused until the sweep (once a minute) has removed the buckets that went
/// quiet; a client already in the table keeps its budget;</item>
/// <item>all clients together: <c>Csp:ReportGlobalPerMinute</c>.</item>
/// </list>
/// A refused report counts against nothing, so a flood cannot starve itself into the window.
/// </summary>
public sealed class CspReportRateLimiter(IOptions<CspOptions> options, TimeProvider time)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _buckets = [with(StringComparer.Ordinal)];
    private readonly Queue<DateTimeOffset> _all = new();
    private readonly int _limit = options.Value.ReportRateLimitPerMinute;
    private readonly int _maxClients = options.Value.ReportMaxClients;
    private readonly int _globalLimit = options.Value.ReportGlobalPerMinute;
    private DateTimeOffset _lastSweep = time.GetUtcNow();

    public bool TryAccept(string clientKey)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (now - _lastSweep >= Window)
            {
                Sweep(now);
                _lastSweep = now;
            }

            Prune(_all, now);
            if (_all.Count >= _globalLimit)
            {
                return false;
            }

            if (!_buckets.TryGetValue(clientKey, out var hits))
            {
                if (_buckets.Count >= _maxClients)
                {
                    return false;
                }

                _buckets[clientKey] = hits = [];
            }

            Prune(hits, now);
            if (hits.Count >= _limit)
            {
                return false;
            }

            hits.Enqueue(now);
            _all.Enqueue(now);
            return true;
        }
    }

    private static void Prune(Queue<DateTimeOffset> hits, DateTimeOffset now)
    {
        while (hits.Count > 0 && now - hits.Peek() >= Window)
        {
            hits.Dequeue();
        }
    }

    private void Sweep(DateTimeOffset now)
    {
        List<string> keys = [.. _buckets.Keys];
        foreach (var key in keys)
        {
            var hits = _buckets[key];
            while (hits.Count > 0 && now - hits.Peek() >= Window)
            {
                hits.Dequeue();
            }

            if (hits.Count == 0)
            {
                _buckets.Remove(key);
            }
        }
    }
}
