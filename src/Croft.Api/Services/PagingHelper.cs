using Croft.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Croft.Api.Services;

public static class PagingHelper
{
    public const int MaxPageSize = 100;

    /// <summary>The last page an anonymous read of a list may ask for: past it the API answers an empty page
    /// without querying or caching (at the largest page size that is 100,000 rows).</summary>
    public const int MaxPublicPage = 1000;

    /// <summary>The page size the query uses for a requested one: the same clamp the cache keys use.</summary>
    public static int ClampPageSize(int pageSize) => Math.Clamp(pageSize, 1, MaxPageSize);

    public static bool IsBeyondPublicPages(int page) => page > MaxPublicPage;

    public static async Task<PagedResult<T>> ToPagedResultAsync<T>(
        this IQueryable<T> query,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = ClampPageSize(pageSize);

        var total = await query.CountAsync(ct);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<T>(items, total, page, pageSize);
    }
}
