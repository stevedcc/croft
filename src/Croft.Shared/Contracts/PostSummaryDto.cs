using Croft.Shared.Enums;

namespace Croft.Shared.Contracts;

/// <summary>UpdatedAt is the post's last-change time in UTC; the sitemap's <c>lastmod</c> is built from it.
/// Excerpt and AuthorName feed the Atom feed: the entry summary falls back through the excerpt
/// (SeoDescription) and the entry author is the post's author. NavigationPlacement and
/// NavigationOrder are the page's footer/sidebar placement, shown in the admin Pages list.</summary>
public record PostSummaryDto(Guid Id, string Slug, string Title, string? Subtitle, string? Excerpt, string Summary,
    int ReadingMinutes, PostStatus Status, DateTime? PublishedAt, DateTime UpdatedAt, string AuthorName, CategoryDto? Category,
    IReadOnlyList<TagDto> Tags, BannerRefDto? Banner, long? ViewCount, int? OpenFeedbackCount,
    PostKind Kind = PostKind.Post, NavigationPlacement NavigationPlacement = NavigationPlacement.None,
    int NavigationOrder = 0);
