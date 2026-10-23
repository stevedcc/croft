using Croft.Shared.Contracts.Social;

namespace Croft.Api.Models;

/// <summary>
/// A hidden reply or account (the owner's moderation record). Not tied to a post and so not deleted with one;
/// it applies before storing, on every refresh.
/// </summary>
public class SocialBlock
{
    public Guid Id { get; set; }
    public SocialPlatform Platform { get; set; }
    public SocialBlockKind Kind { get; set; }

    /// <summary>The status id / at:// URI for <see cref="SocialBlockKind.Reply"/>, the account id / DID for <see cref="SocialBlockKind.Account"/>.</summary>
    public string ExternalId { get; set; } = string.Empty;

    /// <summary>What the admin saw (display name and handle), so the blocks page is readable later.</summary>
    public string? Label { get; set; }

    public string? Note { get; set; }

    /// <summary>The staff user's name.</summary>
    public string CreatedBy { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}
