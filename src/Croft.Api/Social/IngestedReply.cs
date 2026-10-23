namespace Croft.Api.Social;

/// <summary>
/// One reply as it comes out of a platform's answer: filtered, its content sanitised, its avatar still a source URL
/// (the avatar is downloaded and replaced by a hash later). <c>ParentExternalId</c> is null for an answer to the post itself.
/// </summary>
public sealed record IngestedReply(
    string ExternalId,
    string? ParentExternalId,
    string AuthorHandle,
    string AuthorDisplayName,
    string AuthorUrl,
    string AuthorAccountId,
    string? AvatarSourceUrl,
    DateTimeOffset CreatedAt,
    string Permalink,
    string ContentHtml,
    string? ContentWarning,
    bool HasMedia);

/// <summary>The owner's blocklist for one platform: reply ids and account ids (Mastodon account id / Bluesky DID) that are never stored.</summary>
public sealed class SocialBlockSet(IEnumerable<string> replyIds, IEnumerable<string> accountIds)
{
    public static SocialBlockSet Empty { get; } = new([], []);

    private readonly HashSet<string> _replies = new(replyIds, StringComparer.Ordinal);
    private readonly HashSet<string> _accounts = new(accountIds, StringComparer.Ordinal);

    public bool IsBlocked(string replyId, string accountId) => _replies.Contains(replyId) || _accounts.Contains(accountId);
}

/// <summary>A platform's answer is not the shape it should be (not JSON, a field missing): the fetch fails and the last snapshot stays.</summary>
public sealed class SocialParseException(string message, Exception? inner = null) : Exception(message, inner);
