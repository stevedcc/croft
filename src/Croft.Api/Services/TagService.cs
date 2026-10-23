using Croft.Api.Data;
using Croft.Api.Errors;
using Croft.Api.Models;
using Croft.Shared.Contracts;
using Croft.Shared.Contracts.Social;
using Croft.Shared.Enums;
using Croft.Shared.Social;
using Microsoft.EntityFrameworkCore;

namespace Croft.Api.Services;

public class TagService(BlogDbContext db, ISlugService slugService, IPublicReadCache cache, ICacheNotifier cacheNotifier, IHttpContextAccessor httpContextAccessor) : ITagService
{
    /// <summary>Only an anonymous read comes from the cache (the post service's boundary): staff read their own writes.</summary>
    private bool CacheablePublicRead() =>
        PublicReadRequest.IsAnonymousRead(httpContextAccessor.HttpContext);

    public Task<IReadOnlyList<TagDto>> ListAsync(CancellationToken ct = default) =>
        CacheablePublicRead()
            ? cache.GetOrCreateAsync(CacheGroup.Tags, "tags:list", LoadListAsync, ct)
            : LoadListAsync(ct);

    private async Task<IReadOnlyList<TagDto>> LoadListAsync(CancellationToken ct)
    {
        return await db.Tags
            .AsNoTracking()
            .OrderBy(t => t.Name)
            .Select(t => new TagDto(
                t.Id,
                t.Name,
                t.Slug,
                t.Posts.Count(p => p.Status == PostStatus.Published),
                t.CountsTowardsRelated,
                t.Hashtags))
            .ToListAsync(ct);
    }

    public async Task<PagedResult<TagDto>> ListAsync(int page, int pageSize, string? q, CancellationToken ct = default)
    {
        var query = db.Tags.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
        {
            // The explicit escape matters: with the two-argument ILike the pattern's backslash is
            // literal and a user's % would act as a wildcard.
            var pattern = LikeSearch.ContainsPattern(q.Trim());
            query = query.Where(t => EF.Functions.ILike(t.Name, pattern, "\\"));
        }

        return await query
            .OrderBy(t => t.Name)
            .Select(t => new TagDto(
                t.Id,
                t.Name,
                t.Slug,
                t.Posts.Count(p => p.Status == PostStatus.Published),
                t.CountsTowardsRelated,
                t.Hashtags))
            .ToPagedResultAsync(page, pageSize, ct);
    }

    public Task<TagDto?> GetBySlugAsync(string slug, CancellationToken ct = default) =>
        CacheablePublicRead()
            ? cache.GetOrCreateAsync(CacheGroup.Tags, $"tags:slug:{slug}", token => LoadBySlugAsync(slug, token), ct)
            : LoadBySlugAsync(slug, ct);

    private async Task<TagDto?> LoadBySlugAsync(string slug, CancellationToken ct)
    {
        return await db.Tags
            .AsNoTracking()
            .Where(t => t.Slug == slug)
            .Select(t => new TagDto(
                t.Id,
                t.Name,
                t.Slug,
                t.Posts.Count(p => p.Status == PostStatus.Published),
                t.CountsTowardsRelated,
                t.Hashtags))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<TagDto> CreateAsync(string name, CancellationToken ct = default)
    {
        ValidateRequest(name);

        var slug = slugService.GenerateSlug(name);
        var existing = await db.Tags.FirstOrDefaultAsync(t => t.Slug == slug, ct);
        if (existing is not null)
            return await MapToDtoAsync(existing, ct);

        var tag = new Tag { Name = name, Slug = slug };
        db.Tags.Add(tag);
        await cacheNotifier.SaveAndNotifyAsync(db, CacheGroup.All, ct);
        return await MapToDtoAsync(tag, ct);
    }

    public async Task<TagDto?> UpdateAsync(Guid id, UpdateTagRequest request, CancellationToken ct = default)
    {
        ValidateRequest(request.Name);
        List<HashtagDto>? hashtags = null;
        if (request.Hashtags is not null)
        {
            hashtags = Hashtags.TryNormalize(request.Hashtags, "Hashtags", out var normalized, out var error)
                ? normalized
                : throw new ValidationException(error!);
        }

        var tag = await db.Tags.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tag is null)
            return null;

        tag.Name = request.Name;
        tag.Slug = slugService.GenerateSlug(request.Name);
        if (request.CountsTowardsRelated is { } countsTowardsRelated)
        {
            tag.CountsTowardsRelated = countsTowardsRelated;
        }

        if (hashtags is not null)
            tag.Hashtags = hashtags;

        await cacheNotifier.SaveAndNotifyAsync(db, CacheGroup.All, ct);
        return await MapToDtoAsync(tag, ct);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var tag = await db.Tags.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tag is null)
            return false;

        var inUse = await db.Posts.AnyAsync(p => p.Tags.Any(t => t.Id == id), ct);
        if (inUse)
            throw new ConflictException("Cannot delete: tag is used by one or more posts.");

        db.Tags.Remove(tag);
        await cacheNotifier.SaveAndNotifyAsync(db, CacheGroup.All, ct);
        return true;
    }

    private async Task<TagDto> MapToDtoAsync(Tag tag, CancellationToken ct)
    {
        var postCount = await db.Posts.CountAsync(
            p => p.Tags.Any(t => t.Id == tag.Id) && p.Status == PostStatus.Published, ct);

        return new TagDto(tag.Id, tag.Name, tag.Slug, postCount, tag.CountsTowardsRelated, tag.Hashtags);
    }

    private void ValidateRequest(string name) => TaxonomyName.Validate(name, slugService);
}
