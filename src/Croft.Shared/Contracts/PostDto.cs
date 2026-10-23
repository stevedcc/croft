using Croft.Shared.Enums;

namespace Croft.Shared.Contracts;

/// <summary>
/// A post or page as the API returns it. <c>AuthorId</c> is the author's IdP subject: staff reads
/// carry it (the editor uses it), public reads leave it <c>null</c> (and so does the public read
/// cache), because an anonymous reader has no use for an identity-provider identifier.
/// </summary>
public record PostDto(Guid Id, string Slug, string Title, string? Subtitle, string MarkdownContent,
    string? Excerpt, string? Summary, PostStatus Status, DateTime? PublishedAt, DateTime CreatedAt,
    DateTime UpdatedAt, string? AuthorId, string AuthorName, MediaAssetDto? BannerImage,
    IReadOnlyList<TagDto> Tags, CategoryDto? Category, int ReadingMinutes,
    PostKind Kind = PostKind.Post, NavigationPlacement NavigationPlacement = NavigationPlacement.None, int NavigationOrder = 0,
    // DateTime? = null, not DateTime = default: a value-type parameter default is emitted as a
    // null metadata constant, which the OpenAPI schema export then tries to write as a DateTime
    // and fails the whole document. Hand-built DTOs (tests) may leave it unset; the API always
    // fills it from the entity.
    DateTime? ContentEditedAt = null,
    // The linked announcements (brief 12a); null when the post has no discussion link on that network.
    string? MastodonUrl = null,
    string? BlueskyUrl = null,
    // Announcing on publish (brief 12d). Staff reads carry them; public reads leave them empty (like AuthorId), and so does the public read cache.
    string? AnnouncementText = null,
    bool AnnounceMastodon = false,
    bool AnnounceBluesky = false);
