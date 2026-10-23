using Croft.Api.Data;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Microsoft.EntityFrameworkCore;

namespace Croft.Api.Services;

/// <summary>
/// The sitemap's one read (<c>GET /api/sitemap</c>, brief 14b, architecture finding 12): slugs and
/// <c>UpdatedAt</c> of the published posts and pages, and the slugs of the categories and tags that
/// have at least one published post, one slim query for each kind. It replaces four paged walks (a
/// hundred rows a request) over the full list endpoints, and returns the rows those walks produced,
/// in the same order: posts newest first, pages by slug, categories and tags by name, with the same
/// published-only rules (a post or page is published when its status says so and its publish time has
/// come; a category or tag counts when a post of status Published carries it).
/// An anonymous read is cached with the posts (every write drops all groups), like the other public
/// reads; staff read their own writes.
/// </summary>
public class SitemapQueryService(
    BlogDbContext db, TimeProvider time, IPublicReadCache cache, IHttpContextAccessor httpContextAccessor) : ISitemapQueryService
{
    private const string CacheKey = "sitemap";

    public Task<SitemapDto> GetAsync(CancellationToken ct = default) =>
        PublicReadRequest.IsAnonymousRead(httpContextAccessor.HttpContext)
            ? cache.GetOrCreateAsync(CacheGroup.Posts, CacheKey, LoadAsync, ct)
            : LoadAsync(ct);

    private async Task<SitemapDto> LoadAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;

        var posts = await db.Posts
            .AsNoTracking()
            .Where(p => p.Kind == PostKind.Post && p.Status == PostStatus.Published && p.PublishedAt <= now)
            .OrderByDescending(p => p.PublishedAt)
            .Select(p => new SitemapEntryDto(p.Slug, p.UpdatedAt))
            .ToListAsync(ct);

        var pages = await db.Posts
            .AsNoTracking()
            .Where(p => p.Kind == PostKind.Page && p.Status == PostStatus.Published && p.PublishedAt <= now)
            .OrderBy(p => p.Slug)
            .Select(p => new SitemapEntryDto(p.Slug, p.UpdatedAt))
            .ToListAsync(ct);

        var categories = await db.Categories
            .AsNoTracking()
            .Where(c => c.Posts.Any(p => p.Status == PostStatus.Published))
            .OrderBy(c => c.Name)
            .Select(c => c.Slug)
            .ToListAsync(ct);

        var tags = await db.Tags
            .AsNoTracking()
            .Where(t => t.Posts.Any(p => p.Status == PostStatus.Published))
            .OrderBy(t => t.Name)
            .Select(t => t.Slug)
            .ToListAsync(ct);

        return new SitemapDto(posts, pages, categories, tags);
    }
}
