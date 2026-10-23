using Croft.Shared.Contracts;

namespace Croft.Api.Services;

public interface ICategoryService
{
    /// <summary>The legacy full listing (name order); the paged overload is the admin contract.</summary>
    Task<IReadOnlyList<CategoryDto>> ListAsync(CancellationToken ct = default);

    /// <summary>One page of categories (name order); <c>q</c> filters names case-insensitively.</summary>
    Task<PagedResult<CategoryDto>> ListAsync(int page, int pageSize, string? q, CancellationToken ct = default);

    /// <summary>The sidebar's bounded read: categories with published posts, most posts first.</summary>
    Task<IReadOnlyList<CategoryDto>> TopAsync(int count, CancellationToken ct = default);
    Task<CategoryDto?> GetBySlugAsync(string slug, CancellationToken ct = default);
    Task<CategoryDto> CreateAsync(string name, CancellationToken ct = default);
    Task<CategoryDto?> UpdateAsync(Guid id, UpdateCategoryRequest request, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
}
