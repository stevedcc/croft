using System.Security.Cryptography;
using System.Text;

namespace Croft.Api.Services;

public static class AnalyticsIngestKey
{
    /// <summary>
    /// Fixed-time comparison of a provided ingest key against the configured one. Hashing both
    /// values first keeps the comparison constant-time even when lengths differ. An unconfigured
    /// key never matches: X-Visitor-IP is then ignored.
    /// </summary>
    public static bool Matches(string? provided, string? configured)
    {
        if (string.IsNullOrEmpty(configured))
            return false;

        var providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(provided ?? string.Empty));
        var configuredHash = SHA256.HashData(Encoding.UTF8.GetBytes(configured));
        return CryptographicOperations.FixedTimeEquals(providedHash, configuredHash);
    }
}
