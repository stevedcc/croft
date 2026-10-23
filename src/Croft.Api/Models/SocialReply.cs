using Croft.Shared.Contracts.Social;

namespace Croft.Api.Models;

/// <summary>
/// One stored, already filtered and sanitised reply (brief 12a). Only what the discussion shows is
/// kept: no counts, no other metadata. A refresh replaces a thread's rows, so what disappeared upstream disappears here.
/// </summary>
public class SocialReply
{
    public Guid PostId { get; set; }
    public Post Post { get; set; } = null!;
    public SocialPlatform Platform { get; set; }

    /// <summary>The Mastodon status id, or the Bluesky at:// URI.</summary>
    public string ExternalId { get; set; } = string.Empty;

    /// <summary>The reply this answers; null for a top-level reply (an answer to the post itself).</summary>
    public string? ParentExternalId { get; set; }

    public string AuthorHandle { get; set; } = string.Empty;
    public string AuthorDisplayName { get; set; } = string.Empty;
    public string AuthorUrl { get; set; } = string.Empty;

    /// <summary>The Mastodon account id, or the Bluesky DID: what an account block matches.</summary>
    public string AuthorAccountId { get; set; } = string.Empty;

    public string? AvatarHash { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Permalink { get; set; } = string.Empty;
    public string ContentHtml { get; set; } = string.Empty;
    public string? ContentWarning { get; set; }
    public bool HasMedia { get; set; }
    public DateTime FetchedAt { get; set; }
}
