using Croft.Shared.Enums;

namespace Croft.Shared.Contracts;

/// <summary>
/// A new post. <c>AnnouncementText</c>, <c>AnnounceMastodon</c> and <c>AnnounceBluesky</c> (brief 12d): the flags left out (null) are on for a post and off for a page;
/// a page that asks for announcing is refused.
/// </summary>
public record CreatePostRequest(string Title, string MarkdownContent, string? Subtitle = null,
    string? Excerpt = null, Guid? BannerImageId = null, IReadOnlyList<string>? Tags = null, Guid? CategoryId = null,
    PostKind Kind = PostKind.Post, NavigationPlacement NavigationPlacement = NavigationPlacement.None, int NavigationOrder = 0,
    string? MastodonUrl = null, string? BlueskyUrl = null,
    string? AnnouncementText = null, bool? AnnounceMastodon = null, bool? AnnounceBluesky = null);
