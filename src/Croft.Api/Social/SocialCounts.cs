namespace Croft.Api.Social;

/// <summary>The platform's own like, repost and reply counts of one announcement.</summary>
public sealed record SocialCounts(int Likes, int Reposts, int Replies);
