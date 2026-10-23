namespace Croft.Shared.Contracts.Social;

/// <summary>
/// One reply, with its answers nested to depth 3 (deeper ones are flattened into level 3 by <c>CreatedAt</c>).
/// <c>ExternalId</c> is the Mastodon status id or the Bluesky at:// URI; <c>AuthorAccountId</c> the platform's account id
/// or DID (what an account block matches); <c>AvatarHash</c> the hash in <c>/social/avatar/{hash}.webp</c> (the Web serves it),
/// null meaning the fallback avatar; <c>ContentHtml</c> is sanitised at ingest (only p, br and a with
/// rel="nofollow ugc noopener") and ready to render as it is.
/// </summary>
public sealed record DiscussionReplyDto(
    SocialPlatform Platform,
    string ExternalId,
    string AuthorHandle,
    string AuthorDisplayName,
    string AuthorUrl,
    string AuthorAccountId,
    string? AvatarHash,
    DateTimeOffset CreatedAt,
    string Permalink,
    string ContentHtml,
    string? ContentWarning,
    bool HasMedia,
    IReadOnlyList<DiscussionReplyDto> Replies);
