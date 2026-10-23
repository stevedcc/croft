namespace Croft.Web;

/// <summary>
/// Brief 11a item 2: <c>.well-known/security.txt</c> (RFC 9116) is parsed for its
/// <c>Contact</c> and <c>Expires</c> lines; each rule emits exactly one Warning log line.
/// The site must not fail startup or readiness over any of these — the Warning is the only
/// effect. The within-30-days case is logged again, once a day, while it stays so, by a
/// hosted service that ticks on <see cref="TimeProvider"/>.
/// </summary>
/// <remarks>
/// The check is intentionally minimal: it does not try to be a full RFC 9116 parser. The brief
/// asks for the five observable outcomes (missing Contact, missing Expires, Expires in the
/// past, Expires within 30 days, Expires unparsable) and the site must keep running through
/// every one of them — a handful of lines per Warning keep the file focused on what the site
/// actually needs to notice.
/// </remarks>
public sealed partial class SecurityTxtChecker(TimeProvider time, ILogger<SecurityTxtChecker> logger)
{
    private readonly TimeProvider _time = time;
    private readonly ILogger _logger = logger;
    private string? _body;
    private readonly Dictionary<string, DateOnly> _lastEmitted = [];
    private readonly HashSet<string> _active = [];

    /// <summary>Re-evaluate the file. The first call seeds the cached body; later calls
    /// reuse it (the brief is one Warning per condition at startup, plus the daily tick).</summary>
    public void Check(string? body)
    {
        _body = body;
        _lastEmitted.Clear();
        Evaluate();
    }

    /// <summary>The daily tick: re-evaluate the cached body against the now-current clock and
    /// log the Warning if the condition still holds. Called by the hosted service.</summary>
    public Task DailyTickAsync(CancellationToken ct)
    {
        Evaluate();
        return Task.CompletedTask;
    }

    /// <summary>The cached body, exposed for assertions in the hosted service test
    /// (<c>DailyTick_NothingToLog_LogsNothing</c> needs the body to stay valid across ticks).</summary>
    public string? Body => _body;

    private void Evaluate()
    {
        if (_body is null)
        {
            return;
        }

        _active.Clear();
        var hasContact = false;
        DateTimeOffset? expires = null;
        var expiresParseFailed = false;

        foreach (var raw in _body.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.StartsWith("Contact:", StringComparison.Ordinal))
            {
                hasContact = true;
            }
            else if (line.StartsWith("Expires:", StringComparison.Ordinal))
            {
                var value = line["Expires:".Length..].Trim();
                if (DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var parsed))
                {
                    expires = parsed;
                }
                else
                {
                    expiresParseFailed = true;
                }
            }
        }

        if (!hasContact)
        {
            if (ShouldEmit("missing-contact"))
            {
                LogMissingContact(_logger);
            }
        }

        if (expires is null)
        {
            if (expiresParseFailed)
            {
                if (ShouldEmit("unparseable-expires"))
                {
                    LogUnparseableExpires(_logger);
                }
            }
            else
            {
                if (ShouldEmit("missing-expires"))
                {
                    LogMissingExpires(_logger);
                }
            }
        }
        else
        {
            var days = (expires.Value - _time.GetUtcNow()).TotalDays;
            if (days < 0)
            {
                // The key carries every value the message shows, so a changed date or day count is a new message.
                if (ShouldEmit($"expired:{expires.Value:yyyy-MM-dd}"))
                {
                    LogExpired(_logger, expires.Value);
                }
            }
            else if (days <= 30)
            {
                if (ShouldEmit($"expiring:{expires.Value:yyyy-MM-dd}:{days:0}"))
                {
                    LogExpiresSoon(_logger, expires.Value, days);
                }
                // BREAK-ON: change 30 to 0 (or any value <= the days-to-expiry); the 29-days
                // test must then no longer fire the warning (the threshold is below the sample),
                // proving the rule actually runs. Reverted immediately afterwards.
            }
        }

        // A condition that no longer holds is forgotten, so it warns again at once if it returns.
        foreach (var key in _lastEmitted.Keys.ToArray())
        {
            if (!_active.Contains(key))
            {
                _lastEmitted.Remove(key);
            }
        }
    }

    /// <summary>Whether the condition behind <paramref name="key"/> should warn now: once per key per UTC day,
    /// so the startup check and the first tick on the same day do not both log, and two conditions that are
    /// active together each warn once a day rather than one hiding the other (the dedup is per condition,
    /// not against the last line).</summary>
    private bool ShouldEmit(string key)
    {
        _active.Add(key);
        var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        if (_lastEmitted.TryGetValue(key, out var last) && last == today)
        {
            return false;
        }

        _lastEmitted[key] = today;
        return true;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "security.txt is missing the required Contact field")]
    private static partial void LogMissingContact(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "security.txt has an Expires value that is not a parseable RFC 3339 / ISO 8601 date")]
    private static partial void LogUnparseableExpires(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "security.txt is missing the required Expires field")]
    private static partial void LogMissingExpires(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "security.txt expired on {ExpiredOn:yyyy-MM-dd}")]
    private static partial void LogExpired(ILogger logger, DateTimeOffset expiredOn);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "security.txt Expires on {ExpiresOn:yyyy-MM-dd} ({Days:0} day(s) from now, within the 30-day warning window)")]
    private static partial void LogExpiresSoon(ILogger logger, DateTimeOffset expiresOn, double days);
}
