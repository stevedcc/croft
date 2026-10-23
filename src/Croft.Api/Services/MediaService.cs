using Croft.Api.Data;
using Croft.Api.Errors;
using Croft.Api.Models;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;

namespace Croft.Api.Services;

public partial class MediaService(BlogDbContext db, IFileStorage fileStorage, ILogger<MediaService> logger, TimeProvider time, ICacheNotifier cacheNotifier) : IMediaService
{
    private const int MaxAltTextLength = 300;

    private static readonly HashSet<string> AllowedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp"
    };

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public async Task<MediaAssetDto> UploadAsync(
        IFormFile file,
        Guid? postId,
        MediaAssetScope scope,
        string userId,
        string userName,
        CancellationToken ct = default)
    {
        if (scope == MediaAssetScope.PostSpecific && postId.HasValue)
        {
            var postExists = await db.Posts.AnyAsync(p => p.Id == postId.Value, ct);
            if (!postExists)
                throw new NotFoundException($"Post {postId.Value} not found.");
        }

        var extension = Path.GetExtension(file.FileName);
        if (!AllowedImageExtensions.Contains(extension))
            throw new ValidationException("Only image files are allowed.");

        if (!ContentTypes.TryGetContentType(file.FileName, out var contentType))
            contentType = "application/octet-stream";

        var storagePath = scope == MediaAssetScope.PostSpecific && postId.HasValue
            ? $"posts/{postId.Value:N}"
            : "global";

        // The multipart section is a seekable stream: the header is read for the magic-byte check, the
        // stream is rewound, and storage copies it to its file without a second in-memory buffer.
        using var uploadStream = file.OpenReadStream();
        ValidateMagicBytes(uploadStream, extension);

        uploadStream.Position = 0;
        var relativePath = await fileStorage.SaveAsync(uploadStream, file.FileName, contentType, storagePath, ct);

        var asset = new MediaAsset
        {
            FileName = Path.GetFileName(relativePath),
            OriginalName = file.FileName,
            ContentType = contentType,
            FilePath = relativePath,
            UploadedAt = time.GetUtcNow().UtcDateTime,
            UploadedBy = userId,
            Scope = scope,
            PostId = scope == MediaAssetScope.PostSpecific ? postId : null
        };

        try
        {
            db.MediaAssets.Add(asset);
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            fileStorage.Delete(relativePath);
            throw;
        }

        return MapToDto(asset);
    }

    public async Task<MediaAssetDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var asset = await db.MediaAssets
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == id, ct);

        return asset is null ? null : MapToDto(asset);
    }

    public async Task<IReadOnlyList<MediaAssetDto>> ListForPostAsync(Guid postId, CancellationToken ct = default)
    {
        var assets = await db.MediaAssets
            .AsNoTracking()
            .Where(m => m.PostId == postId)
            .OrderBy(m => m.UploadedAt)
            .ToListAsync(ct);

        return [.. assets.Select(MapToDto)];
    }

    public async Task<PagedResult<MediaAssetDto>> ListForPostAsync(Guid postId, int page, int pageSize, string? q, CancellationToken ct = default)
    {
        var query = db.MediaAssets.AsNoTracking().Where(m => m.PostId == postId);
        return await PageAsync(query, page, pageSize, q, ct);
    }

    public async Task<IReadOnlyList<MediaAssetDto>> ListGlobalAsync(CancellationToken ct = default)
    {
        var assets = await db.MediaAssets
            .AsNoTracking()
            .Where(m => m.Scope == MediaAssetScope.Global)
            .OrderByDescending(m => m.UploadedAt)
            .ToListAsync(ct);

        return [.. assets.Select(MapToDto)];
    }

    public async Task<PagedResult<MediaAssetDto>> ListGlobalAsync(int page, int pageSize, string? q, CancellationToken ct = default)
    {
        var query = db.MediaAssets.AsNoTracking().Where(m => m.Scope == MediaAssetScope.Global);
        return await PageAsync(query, page, pageSize, q, ct);
    }

    /// <summary>One page of the filtered query, newest first. Id breaks upload-time ties: ids are
    /// time-ordered UUIDs, so same-instant uploads still page in a stable, newest-first order.</summary>
    private async Task<PagedResult<MediaAssetDto>> PageAsync(
        IQueryable<MediaAsset> query, int page, int pageSize, string? q, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = LikeSearch.ContainsPattern(q.Trim());
            query = query.Where(m => EF.Functions.ILike(m.FileName, pattern, "\\") || EF.Functions.ILike(m.OriginalName, pattern, "\\"));
        }

        var paged = await query
            .OrderByDescending(m => m.UploadedAt)
            .ThenByDescending(m => m.Id)
            .ToPagedResultAsync(page, pageSize, ct);

        return new PagedResult<MediaAssetDto>(
            [.. paged.Items.Select(MapToDto)], paged.TotalCount, paged.Page, paged.PageSize);
    }

    public async Task<MediaAssetDto?> UpdateAsync(Guid id, UpdateMediaRequest request, CancellationToken ct = default)
    {
        if (request.AltText?.Length > MaxAltTextLength)
            throw new ValidationException($"Alt text must be {MaxAltTextLength} characters or fewer.");

        var asset = await db.MediaAssets.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (asset is null)
            return null;

        asset.AltText = request.AltText;
        // Alt text travels inside post DTOs (banners), so this is a content write.
        await cacheNotifier.SaveAndNotifyAsync(db, CacheGroup.All, ct);
        return MapToDto(asset);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var asset = await db.MediaAssets.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (asset is null)
            return false;

        var bannerPostTitles = await db.Posts
            .Where(p => p.BannerImageId == id)
            .Select(p => p.Title)
            .ToListAsync(ct);

        if (bannerPostTitles.Count > 0)
            throw new ConflictException($"Cannot delete: used as a banner image by {string.Join(", ", bannerPostTitles)}.");

        db.MediaAssets.Remove(asset);
        // The brief's one rule: every content write invalidates all four groups.
        await cacheNotifier.SaveAndNotifyAsync(db, CacheGroup.All, ct);

        try
        {
            fileStorage.Delete(asset.FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogFailedToDeleteMediaFile(logger, ex, asset.FilePath, id);
        }

        return true;
    }

    private static void ValidateMagicBytes(Stream stream, string extension)
    {
        const int headerLength = 12;
        if (stream.Length < headerLength)
            throw new ValidationException("File is too small to be a valid image.");

        var header = new byte[headerLength];
        stream.ReadExactly(header, 0, headerLength);

        var isValid = extension.ToLowerInvariant() switch
        {
            ".png" => header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47,
            ".jpg" or ".jpeg" => header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF,
            ".gif" => header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x38,
            ".webp" => header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46
                       && header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50,
            _ => false
        };

        if (!isValid)
            throw new ValidationException("File contents do not match the allowed image format.");
    }

    private static string GetPublicUrl(Guid id)
    {
        return $"/api/media/{id}";
    }

    private MediaAssetDto MapToDto(MediaAsset asset)
    {
        return new MediaAssetDto(
            asset.Id,
            asset.FileName,
            asset.OriginalName,
            asset.ContentType,
            GetPublicUrl(asset.Id),
            asset.Scope,
            asset.PostId,
            asset.AltText);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to delete media file {FilePath} for media asset {MediaId}")]
    private static partial void LogFailedToDeleteMediaFile(ILogger logger, Exception exception, string filePath, Guid mediaId);
}
