using Croft.Api.Data;
using Croft.Api.Identity;
using Croft.Api.Services;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Croft.Api.Controllers;

[ApiController]
[Route("api/media")]
public partial class MediaController(IMediaService mediaService, IFileStorage fileStorage, BlogDbContext db, ILogger<MediaController> logger) : ControllerBase
{
    [HttpPost]
    [Authorize(Roles = "Admin")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 10 * 1024 * 1024)]
    public async Task<IActionResult> Upload(
        IFormFile file,
        [FromForm] Guid? postId,
        [FromForm] MediaAssetScope scope = MediaAssetScope.Global,
        CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        var userName = User.GetUserName();
        var asset = await mediaService.UploadAsync(file, postId, scope, userId, userName, ct);
        return Created($"/api/media/{asset.Id}", asset);
    }

    [HttpGet]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> List(int? page, int? pageSize, string? q, CancellationToken ct)
    {
        // With any of page/pageSize/q this is the paged contract (PagedResult, newest first);
        // without them the legacy full array (seed scripts and raw callers parse it).
        if (page.HasValue || pageSize.HasValue || q is not null)
            return Ok(await mediaService.ListGlobalAsync(page ?? 1, pageSize ?? 10, q, ct));

        var assets = await mediaService.ListGlobalAsync(ct);
        return Ok(assets);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(Guid id, UpdateMediaRequest request, CancellationToken ct)
    {
        var asset = await mediaService.UpdateAsync(id, request, ct);
        return asset is null ? NotFound() : Ok(asset);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var deleted = await mediaService.DeleteAsync(id, ct);
        return deleted ? NoContent() : NotFound();
    }

    [HttpGet("{id:guid}")]
    [AllowAnonymous]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var asset = await db.MediaAssets.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);
        if (asset is null)
            return NotFound();

        // Only the owning post's status decides who may see the file: a projection, not the Post
        // row with its Markdown (up to 200,000 characters) and search vector, on every image request.
        PostStatus? ownerStatus = null;
        if (asset.Scope == MediaAssetScope.PostSpecific)
        {
            ownerStatus = await db.Posts.AsNoTracking()
                .Where(p => p.Id == asset.PostId)
                .Select(p => (PostStatus?)p.Status)
                .FirstOrDefaultAsync(ct);
            if (ownerStatus is { } status && status != PostStatus.Published)
            {
                if (!User.IsInRole("Admin") && !User.IsInRole("Editor"))
                    return Forbid();
            }
        }

        Stream stream;
        try
        {
            stream = fileStorage.OpenRead(asset.FilePath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // A row without its file (a lost volume, a manual delete): a missing image, not a server
            // fault. A Warning so an operator notices; never an Error. Only reached once the access
            // rules above have passed, so a caller who may not see the asset learns nothing.
            LogFileMissing(asset.Id, asset.FilePath);
            return NotFound();
        }

        Response.Headers["X-Content-Type-Options"] = "nosniff";

        var isPublic = asset.Scope == MediaAssetScope.Global
            || ownerStatus == PostStatus.Published;

        Response.Headers.CacheControl = isPublic
            ? "public, max-age=86400"
            : "no-store";

        return File(stream, asset.ContentType);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Media asset {AssetId} has no file at {FilePath}")]
    private partial void LogFileMissing(Guid assetId, string filePath);
}

[ApiController]
[Route("api/posts/{postId:guid}/media")]
public class PostMediaController(IMediaService mediaService) : ControllerBase
{
    [HttpGet]
    [Authorize(Roles = "Admin,Editor")]
    public async Task<IActionResult> List(Guid postId, int? page, int? pageSize, string? q, CancellationToken ct)
    {
        // With any of page/pageSize/q this is the paged contract (PagedResult, newest first);
        // without them the legacy full listing in upload order.
        if (page.HasValue || pageSize.HasValue || q is not null)
            return Ok(await mediaService.ListForPostAsync(postId, page ?? 1, pageSize ?? 10, q, ct));

        var assets = await mediaService.ListForPostAsync(postId, ct);
        return Ok(assets);
    }
}
