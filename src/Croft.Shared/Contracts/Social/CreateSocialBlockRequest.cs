namespace Croft.Shared.Contracts.Social;

public sealed record CreateSocialBlockRequest(
    SocialPlatform Platform,
    SocialBlockKind Kind,
    string ExternalId,
    string? Label,
    string? Note);
