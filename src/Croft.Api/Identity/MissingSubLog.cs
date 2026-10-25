using System.Collections.Concurrent;

namespace Croft.Api.Identity;

/// <summary>
/// Records, once per issuer, that tokens without a <c>sub</c> claim are being refused: an IdP
/// configured to issue them would otherwise fill the log with one line per request. The issuer comes
/// from a validated token, so the set is bounded by the issuers the API trusts.
/// </summary>
public sealed partial class MissingSubLog(ILogger<MissingSubLog> logger)
{
    private readonly ConcurrentDictionary<string, bool> _warned = [with(StringComparer.Ordinal)];

    public void Refused(string? issuer)
    {
        if (_warned.TryAdd(issuer ?? "unknown", true))
        {
            LogRefused(logger, issuer ?? "unknown");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "A token from issuer {Issuer} carries no sub claim and was refused (401). Further refusals for this issuer are not logged. " +
        "The API identifies users by sub: configure the identity provider to put sub in its access tokens.")]
    private static partial void LogRefused(ILogger logger, string issuer);
}
