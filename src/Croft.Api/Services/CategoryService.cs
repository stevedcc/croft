using Croft.Api.Data;
using Croft.Api.Errors;
using Croft.Api.Models;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace Croft.Api.Services;

public partial class CategoryService(BlogDbContext db, ISlugService slugService, IPublicReadCache cache, ICacheNotifier cacheNotifier, IHttpContextAccessor httpContextAccessor) : ICategoryService
{
    private const int MaxIconLength = 64;

    /// <summary>Only an anonymous read comes from the cache (the post service's boundary): staff read their own writes.</summary>
    private bool CacheablePublicRead() =>
        PublicReadRequest.IsAnonymousRead(httpContextAccessor.HttpContext);

    public Task<IReadOnlyList<CategoryDto>> ListAsync(CancellationToken ct = default) =>
        CacheablePublicRead()
            ? cache.GetOrCreateAsync(CacheGroup.Categories, "categories:list", LoadListAsync, ct)
            : LoadListAsync(ct);

    private async Task<IReadOnlyList<CategoryDto>> LoadListAsync(CancellationToken ct)
    {
        return await db.Categories
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .Select(c => new CategoryDto(
                c.Id,
                c.Name,
                c.Slug,
                c.Icon,
                c.Posts.Count(p => p.Status == PostStatus.Published)))
            .ToListAsync(ct);
    }

    public async Task<PagedResult<CategoryDto>> ListAsync(int page, int pageSize, string? q, CancellationToken ct = default)
    {
        var query = db.Categories.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = LikeSearch.ContainsPattern(q.Trim());
            query = query.Where(c => EF.Functions.ILike(c.Name, pattern, "\\"));
        }

        return await query
            .OrderBy(c => c.Name)
            .Select(c => new CategoryDto(
                c.Id,
                c.Name,
                c.Slug,
                c.Icon,
                c.Posts.Count(p => p.Status == PostStatus.Published)))
            .ToPagedResultAsync(page, pageSize, ct);
    }

    public Task<IReadOnlyList<CategoryDto>> TopAsync(int count, CancellationToken ct = default)
    {
        var take = Math.Clamp(count, 1, PagingHelper.MaxPageSize);
        return CacheablePublicRead()
            ? cache.GetOrCreateAsync(CacheGroup.Categories, $"categories:top:{take}", token => LoadTopAsync(take, token), ct)
            : LoadTopAsync(take, ct);
    }

    private async Task<IReadOnlyList<CategoryDto>> LoadTopAsync(int take, CancellationToken ct)
    {
        // WORKAROUND(W-050, EF Core): filter and order on the count expression before projecting to the DTO
        // Filter and order on the count expression before projecting: EF cannot compose a
        // Where over the projected DTO's computed PostCount.
        return
        [
            .. await db.Categories
                .AsNoTracking()
                .Where(c => c.Posts.Count(p => p.Status == PostStatus.Published) > 0)
                .OrderByDescending(c => c.Posts.Count(p => p.Status == PostStatus.Published))
                .ThenBy(c => c.Name)
                .Take(take)
                .Select(c => new CategoryDto(
                    c.Id,
                    c.Name,
                    c.Slug,
                    c.Icon,
                    c.Posts.Count(p => p.Status == PostStatus.Published)))
                .ToListAsync(ct)
        ];
        // END WORKAROUND(W-050)
    }

    public Task<CategoryDto?> GetBySlugAsync(string slug, CancellationToken ct = default) =>
        CacheablePublicRead()
            ? cache.GetOrCreateAsync(CacheGroup.Categories, $"categories:slug:{slug}", token => LoadBySlugAsync(slug, token), ct)
            : LoadBySlugAsync(slug, ct);

    private async Task<CategoryDto?> LoadBySlugAsync(string slug, CancellationToken ct)
    {
        return await db.Categories
            .AsNoTracking()
            .Where(c => c.Slug == slug)
            .Select(c => new CategoryDto(
                c.Id,
                c.Name,
                c.Slug,
                c.Icon,
                c.Posts.Count(p => p.Status == PostStatus.Published)))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<CategoryDto> CreateAsync(string name, CancellationToken ct = default)
    {
        ValidateRequest(name, icon: null);

        var slug = slugService.GenerateSlug(name);
        var existing = await db.Categories.FirstOrDefaultAsync(c => c.Slug == slug, ct);
        if (existing is not null)
            return await MapToDtoAsync(existing, ct);

        var category = new Category { Name = name, Slug = slug };
        db.Categories.Add(category);
        await cacheNotifier.SaveAndNotifyAsync(db, CacheGroup.All, ct);
        return await MapToDtoAsync(category, ct);
    }

    public async Task<CategoryDto?> UpdateAsync(Guid id, UpdateCategoryRequest request, CancellationToken ct = default)
    {
        ValidateRequest(request.Name, request.Icon);

        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (category is null)
            return null;

        category.Name = request.Name;
        category.Slug = slugService.GenerateSlug(request.Name);
        category.Icon = request.Icon;
        await cacheNotifier.SaveAndNotifyAsync(db, CacheGroup.All, ct);
        return await MapToDtoAsync(category, ct);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (category is null)
            return false;

        var inUse = await db.Posts.AnyAsync(p => p.CategoryId == id, ct);
        if (inUse)
            throw new ConflictException("Cannot delete: category is used by one or more posts.");

        db.Categories.Remove(category);
        await cacheNotifier.SaveAndNotifyAsync(db, CacheGroup.All, ct);
        return true;
    }

    private void ValidateRequest(string name, string? icon)
    {
        TaxonomyName.Validate(name, slugService);

        if (icon is null)
            return;

        if (icon.Length > MaxIconLength)
            throw new ValidationException($"Icon must be {MaxIconLength} characters or fewer.");

        if (!IconName().IsMatch(icon))
            throw new ValidationException("Icon must contain only letters and digits.");
    }

    [GeneratedRegex("^[A-Za-z0-9]+$")]
    private static partial Regex IconName();

    private async Task<CategoryDto> MapToDtoAsync(Category category, CancellationToken ct)
    {
        var postCount = await db.Posts.CountAsync(
            p => p.CategoryId == category.Id && p.Status == PostStatus.Published, ct);

        return new CategoryDto(category.Id, category.Name, category.Slug, category.Icon, postCount);
    }
}
