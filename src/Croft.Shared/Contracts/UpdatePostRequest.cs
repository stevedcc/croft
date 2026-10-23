using Croft.Shared.Enums;

namespace Croft.Shared.Contracts;

/// <summary>
/// An update. <c>MastodonUrl</c> and <c>BlueskyUrl</c> (the post's discussion links): null or absent leaves that link, its thread and its replies
/// unchanged (so a client that does not know the fields, such as an older Web pod, cannot erase them); an empty or whitespace-only string clears
/// the link and deletes that platform's thread and replies; any other value is validated and set.
/// <c>AnnouncementText</c>, <c>AnnounceMastodon</c> and <c>AnnounceBluesky</c> (brief 12d): null leaves them as they are; an empty announcement text goes back to the
/// default (title and subtitle); a text over the limit of a network whose flag is on is refused.
/// </summary>
public record UpdatePostRequest(string Title, string MarkdownContent, string? Subtitle = null,
    string? Excerpt = null, Guid? BannerImageId = null, IReadOnlyList<string>? Tags = null, Guid? CategoryId = null,
    PostKind Kind = PostKind.Post, NavigationPlacement NavigationPlacement = NavigationPlacement.None, int NavigationOrder = 0,
    string? MastodonUrl = null, string? BlueskyUrl = null,
    string? AnnouncementText = null, bool? AnnounceMastodon = null, bool? AnnounceBluesky = null);
