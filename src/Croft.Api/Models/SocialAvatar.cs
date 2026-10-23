namespace Croft.Api.Models;

/// <summary>
/// A downloaded avatar, re-encoded to a small WebP. The bytes live in file storage (never in the database);
/// the key is the SHA-256 (hex) of the WebP bytes, which is also what the public URL carries.
/// </summary>
public class SocialAvatar
{
    public string Hash { get; set; } = string.Empty;

    /// <summary>The URL it was downloaded from: a known URL is never downloaded again.</summary>
    public string SourceUrl { get; set; } = string.Empty;

    public string StoragePath { get; set; } = string.Empty;
    public DateTime FetchedAt { get; set; }
}
