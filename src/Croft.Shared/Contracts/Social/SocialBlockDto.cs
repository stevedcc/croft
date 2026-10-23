namespace Croft.Shared.Contracts.Social;

/// <summary>A hidden reply or account. <c>Label</c> is what the admin saw when hiding it (the owner's moderation record).</summary>
public sealed record SocialBlockDto(
    Guid Id,
    SocialPlatform Platform,
    SocialBlockKind Kind,
    string ExternalId,
    string? Label,
    string? Note,
    string CreatedBy,
    DateTimeOffset CreatedAt);
