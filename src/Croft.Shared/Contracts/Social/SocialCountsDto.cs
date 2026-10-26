namespace Croft.Shared.Contracts.Social;

/// <summary>An announcement's like, repost and reply counts as the platform reports them (summed over platforms on post reads).</summary>
public sealed record SocialCountsDto(int Likes, int Reposts, int Replies);
