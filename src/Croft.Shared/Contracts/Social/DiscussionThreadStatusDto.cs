namespace Croft.Shared.Contracts.Social;

/// <summary>
/// The fetch state of one platform's thread of a post (staff only). <c>Stale</c> is true when the last
/// successful fetch is older than 48 hours: the replies are then no longer shown.
/// </summary>
public sealed record DiscussionThreadStatusDto(
    SocialPlatform Platform,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastAttemptAt,
    string? LastError,
    DateTimeOffset? NextDueAt,
    int StoredReplies,
    bool Stale);
