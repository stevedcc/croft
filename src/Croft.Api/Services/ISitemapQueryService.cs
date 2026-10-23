using Croft.Shared.Contracts;

namespace Croft.Api.Services;

public interface ISitemapQueryService
{
    /// <summary>The slugs and dates the sitemap is built from, in one read (brief 14b).</summary>
    Task<SitemapDto> GetAsync(CancellationToken ct = default);
}
