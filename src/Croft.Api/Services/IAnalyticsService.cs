using Croft.Shared.Contracts;

namespace Croft.Api.Services;

public interface IAnalyticsService
{
    Task RecordPageViewAsync(Guid postId, string? ipAddress, string? userAgent, string? referrer, CancellationToken ct = default);
    Task<AnalyticsSummaryDto> GetSummaryAsync(Guid postId, DateTime? from = null, DateTime? to = null, CancellationToken ct = default);
}
