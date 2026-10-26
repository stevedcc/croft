namespace Croft.Shared.Contracts.Social;

/// <summary>
/// A post's discussion as readers see it: the linked announcements and the merged replies of both platforms.
/// </summary>
/// <param name="MastodonUrl">The linked Mastodon post (the reply action's target); null when not linked.</param>
/// <param name="BlueskyUrl">The linked Bluesky post; null when not linked.</param>
/// <param name="ReplyCount">Every reply in the tree below, nested ones included.</param>
/// <param name="MastodonCounts">The platform's own like, repost and reply counts of the linked Mastodon post; null when unknown.</param>
/// <param name="BlueskyCounts">The same for the linked Bluesky post.</param>
/// <param name="Replies">The top-level replies, both platforms merged by <see cref="DiscussionReplyDto.CreatedAt"/>.</param>
public sealed record PostDiscussionDto(
    string? MastodonUrl,
    string? BlueskyUrl,
    int ReplyCount,
    IReadOnlyList<DiscussionReplyDto> Replies,
    SocialCountsDto? MastodonCounts = null,
    SocialCountsDto? BlueskyCounts = null);
