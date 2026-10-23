using Croft.Shared.Contracts;
using Croft.Shared.Enums;

namespace Croft.Api.Services;

public interface IPostService
{
    Task<PostDto?> GetBySlugAsync(string slug, bool publicOnly, CancellationToken ct = default);
    Task<IReadOnlyList<PostSummaryDto>?> RelatedAsync(string slug, int count, bool publicOnly, bool includeAdminCounts, CancellationToken ct = default);
    Task<PagedResult<PostSummaryDto>> ListAsync(PostListRequest request, bool publicOnly, bool includeAdminCounts, CancellationToken ct = default);
    Task<PostDto> CreateAsync(CreatePostRequest request, string authorId, string authorName, CancellationToken ct = default);
    Task<PostDto?> UpdateAsync(Guid id, UpdatePostRequest request, string userId, bool isAdmin, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
    Task<PostDto?> PublishAsync(Guid id, PublishRequest request, CancellationToken ct = default);
    Task<PostDto?> UnpublishAsync(Guid id, CancellationToken ct = default);

    /// <summary>The entries of one placement (sidebar or footer): published pages with that
    /// placement, NavigationOrder then title. <see cref="NavigationPlacement.None"/> has no list.</summary>
    Task<IReadOnlyList<PageNavItemDto>> GetPageNavigationAsync(
        NavigationPlacement placement, CancellationToken ct = default);

    /// <summary>Both lists from one read (and one cache entry): the shell of every reading page
    /// needs the sidebar's pages and the footer's, and must not cost a second API call.</summary>
    Task<PageNavigationDto> GetPageNavigationsAsync(CancellationToken ct = default);

    /// <summary>The public page fetch: the page behind <paramref name="slug"/>, published only unless <paramref name="publicOnly"/> is false.</summary>
    Task<PostDto?> GetPageBySlugAsync(string slug, bool publicOnly, CancellationToken ct = default);

    /// <summary>The sitemap's page rows: published pages (defensively filtered by the caller too), with their UpdatedAt.</summary>
    Task<PagedResult<PageSummaryDto>> ListPagesAsync(int page, int pageSize, CancellationToken ct = default);
}
