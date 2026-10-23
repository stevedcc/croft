using Croft.Shared.Contracts;

namespace Croft.Api.Services;

public interface ITagService
{
    /// <summary>The legacy full listing (name order); the paged overload is the admin contract.</summary>
    Task<IReadOnlyList<TagDto>> ListAsync(CancellationToken ct = default);

    /// <summary>One page of tags (name order); <c>q</c> filters names case-insensitively.</summary>
    Task<PagedResult<TagDto>> ListAsync(int page, int pageSize, string? q, CancellationToken ct = default);

    /// <summary>One page of the tags with exactly these names (case-insensitive), so the editor can read the hashtags of the tags it only knows by name.</summary>
    Task<PagedResult<TagDto>> ListByNamesAsync(int page, int pageSize, IReadOnlyList<string> names, CancellationToken ct = default);
    Task<TagDto?> GetBySlugAsync(string slug, CancellationToken ct = default);
    Task<TagDto> CreateAsync(string name, CancellationToken ct = default);
    Task<TagDto?> UpdateAsync(Guid id, UpdateTagRequest request, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
}
