namespace Croft.Shared.Contracts;

public record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);
