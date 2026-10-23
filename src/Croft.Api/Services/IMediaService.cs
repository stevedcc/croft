using Croft.Shared.Contracts;
using Croft.Shared.Enums;

namespace Croft.Api.Services;

public interface IMediaService
{
    Task<MediaAssetDto> UploadAsync(IFormFile file, Guid? postId, MediaAssetScope scope, string userId, string userName, CancellationToken ct = default);
    Task<MediaAssetDto?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>The legacy full listing of a post's media (upload order); the paged overload pages newest first.</summary>
    Task<IReadOnlyList<MediaAssetDto>> ListForPostAsync(Guid postId, CancellationToken ct = default);

    /// <summary>One page of a post's media, newest first; <c>q</c> filters file names case-insensitively.</summary>
    Task<PagedResult<MediaAssetDto>> ListForPostAsync(Guid postId, int page, int pageSize, string? q, CancellationToken ct = default);

    /// <summary>The legacy full listing of the global media (newest first); the paged overload is the admin contract.</summary>
    Task<IReadOnlyList<MediaAssetDto>> ListGlobalAsync(CancellationToken ct = default);

    /// <summary>One page of the global media, newest first; <c>q</c> filters file names case-insensitively.</summary>
    Task<PagedResult<MediaAssetDto>> ListGlobalAsync(int page, int pageSize, string? q, CancellationToken ct = default);
    Task<MediaAssetDto?> UpdateAsync(Guid id, UpdateMediaRequest request, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
}
