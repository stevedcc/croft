using Croft.Api.Data;
using Croft.Api.Social;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Microsoft.EntityFrameworkCore;

namespace Croft.Api.Services;

/// <summary>
/// The public full-text search. An anonymous read (<see cref="PublicReadRequest.IsAnonymousRead"/>, the
/// boundary the post and category reads use) goes through <see cref="IPublicReadCache"/>, group Posts, so a
/// repeated search runs no full-text query and a write drops it with everything else (brief 14b,
/// architecture finding 11). The key is built like the list's search term: normalised
/// (<see cref="PostService.NormalizeSearchTerm"/>), page and size clamped (<see cref="PagingHelper"/>);
/// an empty result is cached as a negative, which the cache caps at 200 entries, so random terms cannot
/// take more; pages past <see cref="PostService.MaxCachedSearchPage"/> are not cached and pages nobody
/// serves are answered empty. Staff searches are never cached. The query itself runs on the normalised term
/// (the full-text query ignores case and spacing), so what the key means is what the query asked.
/// </summary>
public class SearchService(
    BlogDbContext db, TimeProvider time, IPublicReadCache cache, IHttpContextAccessor httpContextAccessor, ISocialCountsReader socialCounts) : ISearchService
{
    public async Task<PagedResult<PostSummaryDto>> SearchAsync(string query, bool includeAdminCounts, int page = 1, int pageSize = 10, CancellationToken ct = default)
    {
        var result = await SearchCachedAsync(query, includeAdminCounts, page, pageSize, ct);

        // After the posts cache: a count change invalidates Discussion only (brief 17e). Search finds published posts only.
        return includeAdminCounts ? result : result with { Items = await socialCounts.WithSocialAsync(result.Items, ct) };
    }

    private async Task<PagedResult<PostSummaryDto>> SearchCachedAsync(string query, bool includeAdminCounts, int page, int pageSize, CancellationToken ct)
    {
        var term = PostService.NormalizeSearchTerm(query);
        if (term is null)
            return new PagedResult<PostSummaryDto>([], 0, page, pageSize);

        if (includeAdminCounts || !PublicReadRequest.IsAnonymousRead(httpContextAccessor.HttpContext))
            return await LoadAsync(term, includeAdminCounts, page, pageSize, ct);

        pageSize = PagingHelper.ClampPageSize(pageSize);
        page = Math.Max(1, page);
        if (PagingHelper.IsBeyondPublicPages(page))
            return new PagedResult<PostSummaryDto>([], 0, page, pageSize);

        if (page > PostService.MaxCachedSearchPage)
            return await LoadAsync(term, includeAdminCounts: false, page, pageSize, ct);

        return await cache.GetOrCreateAsync(CacheGroup.Posts,
            PostService.CacheKey("search", term, page, pageSize),
            token => LoadAsync(term, includeAdminCounts: false, page, pageSize, token),
            result => result.Items.Count == 0, ct);
    }

    private async Task<PagedResult<PostSummaryDto>> LoadAsync(string term, bool includeAdminCounts, int page, int pageSize, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var ranked = db.Posts
            .AsNoTracking()
            .Where(p => p.Status == PostStatus.Published && p.PublishedAt <= now)
            .Where(p => p.Kind == PostKind.Post)
            .Where(p => p.SearchVector.Matches(EF.Functions.PlainToTsQuery("english", term)))
            .OrderByDescending(p => p.SearchVector.Rank(EF.Functions.PlainToTsQuery("english", term)));

        var result = await PostService.ToSummaries(ranked, includeAdminCounts).ToPagedResultAsync(page, pageSize, ct);
        return result with { Items = [.. result.Items.Select(PostService.WithTagsSortedByName)] };
    }
}
