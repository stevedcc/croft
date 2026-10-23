using Croft.Shared.Contracts;

namespace Croft.Api.Services;

public interface ISearchService
{
    Task<PagedResult<PostSummaryDto>> SearchAsync(string query, bool includeAdminCounts, int page = 1, int pageSize = 10, CancellationToken ct = default);
}
