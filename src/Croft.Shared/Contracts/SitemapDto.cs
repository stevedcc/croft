namespace Croft.Shared.Contracts;

/// <summary>One post or page in the sitemap: the slug the URL is built from and the <c>UpdatedAt</c>
/// that becomes its <c>lastmod</c>.</summary>
public record SitemapEntryDto(string Slug, DateTime UpdatedAt);

/// <summary>
/// Everything the sitemap is built from, in one anonymous read (<c>GET /api/sitemap</c>, brief 14b):
/// the published posts (newest first), the published pages (by slug), and the slugs of the categories
/// and tags that have at least one published post (by name). Drafts, scheduled posts and empty
/// categories and tags are not in it.
/// </summary>
public record SitemapDto(
    IReadOnlyList<SitemapEntryDto> Posts,
    IReadOnlyList<SitemapEntryDto> Pages,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> Tags);
