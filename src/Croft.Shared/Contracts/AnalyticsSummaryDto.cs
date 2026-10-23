namespace Croft.Shared.Contracts;

public record AnalyticsSummaryDto(
    Guid PostId,
    string Title,
    long TotalViews,
    IReadOnlyList<DailyViewCount> DailyViews);

public record DailyViewCount(DateTime Date, long Count);
