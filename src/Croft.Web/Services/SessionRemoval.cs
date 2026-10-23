using Croft.Web.Telemetry;

namespace Croft.Web.Services;

/// <summary>
/// Every way a login session ends is recorded here, once, with its reason: one Information log
/// entry and one <c>croft.web.session.removed{reason}</c> count. The owner reads these to tell why
/// someone was signed out. Entries carry the first 8 characters of the session id and never a
/// token or the IdP's response body.
/// </summary>
public static partial class SessionRemoval
{
    public const string NoRefreshToken = "NoRefreshToken";
    public const string RefreshRejected = "RefreshRejected";
    public const string EntryMissing = "EntryMissing";
    public const string Purged = "Purged";
    public const string BackChannelLogout = "BackChannelLogout";

    /// <summary>The session expired and has no refresh token to renew it with.</summary>
    public static void LogNoRefreshToken(ILogger logger, string? sessionId)
    {
        var shortId = ShortId(sessionId);
        LogSessionRemoved(logger, shortId, NoRefreshToken);
        WebMetrics.SessionRemoved(NoRefreshToken);
    }

    /// <summary>The IdP's token endpoint rejected the refresh token (400 or 401).</summary>
    public static void LogRefreshRejected(
        ILogger logger, string? sessionId, int status, string? error, string? errorDescription)
    {
        var shortId = ShortId(sessionId);
        var cleanError = Sanitise(error);
        var cleanDescription = Sanitise(errorDescription);
        LogSessionRefreshRejected(logger, shortId, RefreshRejected, status, cleanError, cleanDescription);
        WebMetrics.SessionRemoved(RefreshRejected);
    }

    /// <summary>The login cookie names a session the store no longer has; the cookie is signed out.</summary>
    public static void LogEntryMissing(ILogger logger, string? sessionId)
    {
        var shortId = ShortId(sessionId);
        LogSessionRemoved(logger, shortId, EntryMissing);
        WebMetrics.SessionRemoved(EntryMissing);
    }

    /// <summary>The purge deleted <paramref name="count"/> ended sessions.</summary>
    public static void LogPurged(ILogger logger, int count)
    {
        LogSessionsRemoved(logger, Purged, count);
        WebMetrics.SessionRemoved(Purged, count);
    }

    /// <summary>A back-channel logout ended <paramref name="count"/> sessions.</summary>
    public static void LogBackChannelLogout(ILogger logger, int count)
    {
        LogSessionsRemoved(logger, BackChannelLogout, count);
        WebMetrics.SessionRemoved(BackChannelLogout, count);
    }

    private static string ShortId(string? sessionId) =>
        string.IsNullOrEmpty(sessionId) ? "none" : sessionId[..Math.Min(8, sessionId.Length)];

    /// <summary>
    /// The IdP's error text, cut short and without control characters, so a hostile or odd
    /// response cannot forge log lines or flood the log.
    /// </summary>
    private static string? Sanitise(string? text)
    {
        if (text is null)
        {
            return null;
        }

        return new string([.. text.Take(200).Select(c => char.IsControl(c) ? ' ' : c)]);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId} ended. Reason: {Reason}.")]
    private static partial void LogSessionRemoved(ILogger logger, string sessionId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId} ended. Reason: {Reason} (HTTP {Status}, error {Error}: {ErrorDescription}).")]
    private static partial void LogSessionRefreshRejected(
        ILogger logger, string sessionId, string reason, int status, string? error, string? errorDescription);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} session(s) ended. Reason: {Reason}.")]
    private static partial void LogSessionsRemoved(ILogger logger, string reason, int count);
}
