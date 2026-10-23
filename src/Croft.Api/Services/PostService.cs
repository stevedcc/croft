using System.Globalization;
using System.Linq.Expressions;
using System.Text;
using Croft.Api.Data;
using Croft.Api.Errors;
using Croft.Api.Models;
using Croft.Api.Social;
using Croft.Api.Telemetry;
using Croft.Shared;
using Croft.Shared.Contracts;
using Croft.Shared.Contracts.Social;
using Croft.Shared.Enums;
using Croft.Shared.Social;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Croft.Api.Services;

public partial class PostService(BlogDbContext db, ISlugService slugService, IFileStorage fileStorage, ILogger<PostService> logger, TimeProvider time, IScheduledPublishWakeup scheduledPublishWakeup, ICacheNotifier cacheNotifier, IPublicReadCache cache, IHttpContextAccessor httpContextAccessor, DateTimeNormalizer normalizer, ISocialLinkService socialLinks, IOptions<SocialOptions> socialOptions, IAnnouncementOutbox announcements) : IPostService
{
    private const int MaxTitleLength = 400;

    /// <summary>The most related posts one read returns.</summary>
    private const int MaxRelated = 10;

    /// <summary>A search term longer than this is cut: the cache key and the query use the same text.</summary>
    internal const int MaxSearchTermLength = 100;

    /// <summary>Search results past this page are never cached: nobody pages deep through a search,
    /// and a crawler asking for page after page of every term must not fill the cache.</summary>
    internal const int MaxCachedSearchPage = 3;
    private const int MaxSubtitleLength = 300;

    private static void ValidateRequest(string title, string? subtitle, string content)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ValidationException("Title is required.");

        if (title.Length > MaxTitleLength)
            throw new ValidationException($"Title must be {MaxTitleLength} characters or fewer.");

        if (subtitle?.Length > MaxSubtitleLength)
            throw new ValidationException($"Subtitle must be {MaxSubtitleLength} characters or fewer.");

        if (content.Length > PostContent.MaxLength)
            throw new ValidationException(string.Create(CultureInfo.InvariantCulture,
                $"Content must be {PostContent.MaxLength:N0} characters or fewer."));
    }

    /// <summary>Pages are never announced: a request that asks for it (a flag on, or a text) is refused rather than quietly ignored.</summary>
    private static void ValidatePageAnnouncing(PostKind kind, string? text, bool? announceMastodon, bool? announceBluesky)
    {
        if (kind == PostKind.Page && (announceMastodon == true || announceBluesky == true || !string.IsNullOrWhiteSpace(text)))
            throw new ValidationException("Announcing is not allowed for pages.");
    }

    /// <summary>An announcement text of white space only is no text: the default applies, and nothing is stored.</summary>
    private static string? NormalizeAnnouncementText(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    /// <summary>
    /// A text of the post's own must fit every network whose flag is on, counted the way that network counts, with the post's address added at the end.
    /// Bluesky (the tighter limit) is checked first. The default text is never refused: it is shortened when it is sent.
    /// </summary>
    private void ValidateAnnouncementText(string? text, bool announceMastodon, bool announceBluesky, string slug)
    {
        if (text is null)
            return;

        var composed = AnnouncementText.Compose(text, AnnouncementText.PostUrl(socialOptions.Value.BaseUrl, slug));
        foreach (var (platform, name, on) in new[] { (SocialPlatform.Bluesky, "Bluesky", announceBluesky), (SocialPlatform.Mastodon, "Mastodon", announceMastodon) })
        {
            var length = AnnouncementText.Length(platform, composed);
            if (on && length > AnnouncementText.Limit(platform))
                throw new ValidationException(string.Create(CultureInfo.InvariantCulture,
                    $"AnnouncementText is too long for {name} ({length} of {AnnouncementText.Limit(platform)} characters)."));
        }
    }

    /// <summary>A page lives at the Web root with just its title: it carries no category, tags or banner.</summary>
    private static void ValidatePageShape(PostKind kind, Guid? categoryId, IReadOnlyList<string>? tags, Guid? bannerImageId)
    {
        if (kind != PostKind.Page)
            return;

        if (categoryId is not null)
            throw new ValidationException("Category is not allowed for pages.");

        if (tags is { Count: > 0 })
            throw new ValidationException("Tags are not allowed for pages.");

        if (bannerImageId is not null)
            throw new ValidationException("Banner is not allowed for pages.");
    }

    /// <summary>
    /// A page lives at /<c>slug</c> on the Web root, so its slug must never be one of the Web
    /// app's own first segments. Generation suffixes those away; this guard rejects any page
    /// that carries one anyway (e.g. a row predating the rule).
    /// </summary>
    private static void ValidatePageSlug(PostKind kind, string slug)
    {
        if (kind == PostKind.Page && ReservedSlugs.Contains(slug))
            throw new ValidationException($"Slug '{slug}' is reserved.");
    }

    /// <summary>
    /// Only the anonymous variant of a read is cached — the same public-read boundary the
    /// read-replica routing uses. A signed-in reader, admin or editor always runs their query.
    /// </summary>
    private bool CacheablePublicVariant(bool publicOnly, bool includeAdminCounts) =>
        publicOnly && !includeAdminCounts && CacheablePublicRead();

    private bool CacheablePublicRead() =>
        PublicReadRequest.IsAnonymousRead(httpContextAccessor.HttpContext);

    /// <summary>
    /// Builds a cache key from a name and length-prefixed parts (with a distinct null marker),
    /// so no combination of input values can collide with another and serve the wrong body.
    /// </summary>
    internal static string CacheKey(string name, params object?[] parts)
    {
        var key = new StringBuilder(name);
        foreach (var part in parts)
        {
            key.Append('\u001F');
            if (part is null)
            {
                key.Append('~');
                continue;
            }

            var text = string.Create(CultureInfo.InvariantCulture, $"{part}");
            key.Append('#').Append(text.Length).Append(':').Append(text);
        }

        return key.ToString();
    }

    public async Task<PostDto?> GetBySlugAsync(string slug, bool publicOnly, CancellationToken ct = default)
    {
        if (CacheablePublicVariant(publicOnly, includeAdminCounts: false))
            return await cache.GetOrCreateAsync(CacheGroup.Posts, CacheKey("posts:slug", slug),
                token => LoadBySlugAsync(slug, publicOnly, token), ct);

        return await LoadBySlugAsync(slug, publicOnly, ct);
    }

    private async Task<PostDto?> LoadBySlugAsync(string slug, bool publicOnly, CancellationToken ct)
    {
        var post = await FindOneAsync(p => p.Slug == slug, publicOnly, ct);
        if (post is null && Guid.TryParse(slug, out var id))
        {
            // The admin editor routes by id (/admin/posts/{id}/edit), so the read endpoint
            // resolves an id as well as a slug. The slug is tried first: a title can slugify to
            // something guid-shaped, and its own slug must still win.
            post = await FindOneAsync(p => p.Id == id, publicOnly, ct);
        }

        return post is null ? null : await MapToDtoAsync(post, includeAuthorId: !publicOnly, ct);
    }

    public async Task<IReadOnlyList<PageNavItemDto>> GetPageNavigationAsync(
        NavigationPlacement placement, CancellationToken ct = default)
    {
        var all = await GetPageNavigationsAsync(ct);
        return placement switch
        {
            NavigationPlacement.Sidebar => all.Sidebar,
            NavigationPlacement.Footer => all.Footer,
            _ => []
        };
    }

    public async Task<PageNavigationDto> GetPageNavigationsAsync(CancellationToken ct = default)
    {
        if (CacheablePublicRead())
            return await cache.GetOrCreateAsync(CacheGroup.Pages, "pages:nav", LoadPageNavigationsAsync, ct);

        return await LoadPageNavigationsAsync(ct);
    }

    /// <summary>One query for both lists: published pages placed in the sidebar or the footer,
    /// NavigationOrder then title, split by placement in memory.</summary>
    private async Task<PageNavigationDto> LoadPageNavigationsAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var placed = await db.Posts
            .AsNoTracking()
            .Where(p => p.Kind == PostKind.Page
                && p.Status == PostStatus.Published && p.PublishedAt <= now
                && p.NavigationPlacement != NavigationPlacement.None)
            .OrderBy(p => p.NavigationOrder)
            .ThenBy(p => p.Title)
            .Select(p => new { p.NavigationPlacement, Item = new PageNavItemDto(p.Title, p.Slug) })
            .ToListAsync(ct);

        return new PageNavigationDto(
            [.. placed.Where(p => p.NavigationPlacement == NavigationPlacement.Sidebar).Select(p => p.Item)],
            [.. placed.Where(p => p.NavigationPlacement == NavigationPlacement.Footer).Select(p => p.Item)]);
    }

    public async Task<PostDto?> GetPageBySlugAsync(string slug, bool publicOnly, CancellationToken ct = default)
    {
        if (CacheablePublicVariant(publicOnly, includeAdminCounts: false))
            return await cache.GetOrCreateAsync(CacheGroup.Pages, CacheKey("pages:slug", slug),
                token => LoadPageBySlugAsync(slug, publicOnly, token), ct);

        return await LoadPageBySlugAsync(slug, publicOnly, ct);
    }

    private async Task<PostDto?> LoadPageBySlugAsync(string slug, bool publicOnly, CancellationToken ct)
    {
        var post = await FindOneAsync(p => p.Slug == slug && p.Kind == PostKind.Page, publicOnly, ct);
        return post is null ? null : await MapToDtoAsync(post, includeAuthorId: !publicOnly, ct);
    }

    public async Task<PagedResult<PageSummaryDto>> ListPagesAsync(int page, int pageSize, CancellationToken ct = default)
    {
        if (CacheablePublicRead())
        {
            // The key uses the values the query itself clamps to (PagingHelper), so one result is one entry.
            pageSize = PagingHelper.ClampPageSize(pageSize);
            if (PagingHelper.IsBeyondPublicPages(page))
                return new PagedResult<PageSummaryDto>([], 0, page, pageSize);
            page = Math.Max(1, page);

            return await cache.GetOrCreateAsync(CacheGroup.Pages, CacheKey("pages:list", page, pageSize),
                token => LoadPagesAsync(page, pageSize, token), result => result.Items.Count == 0, ct);
        }

        return await LoadPagesAsync(page, pageSize, ct);
    }

    private async Task<PagedResult<PageSummaryDto>> LoadPagesAsync(int page, int pageSize, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        return await db.Posts
            .AsNoTracking()
            .Where(p => p.Kind == PostKind.Page
                && p.Status == PostStatus.Published && p.PublishedAt <= now)
            .OrderBy(p => p.Slug)
            .Select(p => new PageSummaryDto(p.Id, p.Slug, p.Title, p.Status, p.UpdatedAt))
            .ToPagedResultAsync(page, pageSize, ct);
    }

    private async Task<Post?> FindOneAsync(
        Expression<Func<Post, bool>> match, bool publicOnly, CancellationToken ct)
    {
        var query = db.Posts
            .AsNoTracking()
            .Include(p => p.Tags)
            .Include(p => p.Category)
            .Include(p => p.BannerImage)
            .Where(match);

        if (publicOnly)
        {
            var now = time.GetUtcNow().UtcDateTime;
            query = query.Where(p => p.Status == PostStatus.Published && p.PublishedAt <= now);
        }

        return await query.FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Related posts for the post behind <paramref name="slug"/>, ranked in SQL:
    /// 2 × shared tags that count towards related + 1 for a shared category, ordered by score desc
    /// then <see cref="Post.PublishedAt"/> desc, published posts only. A tag switched off in
    /// "Counts towards related posts" is neutral: it contributes no points and no demotion, so
    /// posts sharing only such a tag rank exactly like posts sharing nothing with the source —
    /// zero-scoring filler, newest first, filling what is left of <paramref name="count"/>.
    /// Returns null when the slug does not exist, or — for a public caller — when the
    /// source post is not published (same boundary as <see cref="GetBySlugAsync"/>).
    /// </summary>
    public async Task<IReadOnlyList<PostSummaryDto>?> RelatedAsync(string slug, int count, bool publicOnly, bool includeAdminCounts, CancellationToken ct = default)
    {
        if (CacheablePublicVariant(publicOnly, includeAdminCounts))
        {
            // The loader clamps the count to 1..10 (MaxRelated); so does the key.
            count = Math.Clamp(count, 1, MaxRelated);
            return await cache.GetOrCreateAsync(CacheGroup.Posts, CacheKey("posts:related", slug, count),
                token => LoadRelatedAsync(slug, count, publicOnly, includeAdminCounts, token), ct);
        }

        return await LoadRelatedAsync(slug, count, publicOnly, includeAdminCounts, ct);
    }

    private async Task<IReadOnlyList<PostSummaryDto>?> LoadRelatedAsync(string slug, int count, bool publicOnly, bool includeAdminCounts, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var sourceQuery = db.Posts
            .AsNoTracking()
            .Where(p => p.Slug == slug);

        if (publicOnly)
            sourceQuery = sourceQuery.Where(p => p.Status == PostStatus.Published && p.PublishedAt <= now);

        var source = await sourceQuery
            .Select(p => new { p.Id, p.CategoryId, TagIds = p.Tags.Select(t => t.Id).ToList() })
            .FirstOrDefaultAsync(ct);

        if (source is null)
            return null;

        var take = Math.Clamp(count, 1, MaxRelated);
        var tagIds = source.TagIds;
        var categoryId = source.CategoryId;

        // The category bonus needs an actual shared category: two posts without one do not match.
        // A tag the owner switched off ("Counts towards related posts") is neutral: it adds no
        // points and no demotion — its posts rank exactly like posts sharing nothing with the
        // source (zero-scoring filler, newest first).
        var ranked = db.Posts
            .AsNoTracking()
            .Where(p => p.Id != source.Id
                && p.Status == PostStatus.Published && p.PublishedAt <= now
                && p.Kind == PostKind.Post)
            .OrderByDescending(p =>
                2 * p.Tags.Count(t => tagIds.Contains(t.Id) && t.CountsTowardsRelated)
                + (categoryId != null && p.CategoryId == categoryId ? 1 : 0))
            .ThenByDescending(p => p.PublishedAt)
            .Take(take);

        var summaries = await ToSummaries(ranked, includeAdminCounts).ToListAsync(ct);
        return [.. summaries.Select(WithTagsSortedByName)];
    }

    public async Task<PagedResult<PostSummaryDto>> ListAsync(PostListRequest request, bool publicOnly, bool includeAdminCounts, CancellationToken ct = default)
    {
        if (!CacheablePublicVariant(publicOnly, includeAdminCounts))
            return await LoadListAsync(request, publicOnly, includeAdminCounts, ct);

        // Every user-controlled part of the key is clamped to what the query itself would use, so
        // one result is one key and a crawler cannot mint keys: ?pageSize=100, =101 and =99999 are
        // one entry, a search term differing in case or spacing is one entry, and a page past the
        // last the site serves is an empty page that is neither queried nor cached.
        request = request with
        {
            PageSize = PagingHelper.ClampPageSize(request.PageSize),
            Page = Math.Max(1, request.Page),
            SearchTerm = NormalizeSearchTerm(request.SearchTerm),
        };
        if (PagingHelper.IsBeyondPublicPages(request.Page))
            return new PagedResult<PostSummaryDto>([], 0, request.Page, request.PageSize);

        if (request.SearchTerm is not null && request.Page > MaxCachedSearchPage)
            return await LoadListAsync(request, publicOnly, includeAdminCounts, ct);

        return await cache.GetOrCreateAsync(CacheGroup.Posts,
            CacheKey("posts:list", request.SearchTerm, request.TagSlug, request.CategorySlug,
                request.Status, request.Kind, request.Page, request.PageSize),
            token => LoadListAsync(request, publicOnly, includeAdminCounts, token),
            result => result.Items.Count == 0, ct);
    }

    /// <summary>Trimmed, whitespace collapsed to single spaces, lower-cased and cut to
    /// <see cref="MaxSearchTermLength"/>; null when nothing is left (the full-text query ignores case and spacing, so the result is the same).</summary>
    internal static string? NormalizeSearchTerm(string? term)
    {
        if (string.IsNullOrWhiteSpace(term))
            return null;

        var collapsed = string.Join(' ', term.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var lower = collapsed.ToLowerInvariant();
        return (lower.Length > MaxSearchTermLength ? lower[..MaxSearchTermLength] : lower).TrimEnd();
    }

    private async Task<PagedResult<PostSummaryDto>> LoadListAsync(PostListRequest request, bool publicOnly, bool includeAdminCounts, CancellationToken ct)
    {
        var query = db.Posts
            .AsNoTracking()
            .AsQueryable();

        if (publicOnly)
        {
            var now = time.GetUtcNow().UtcDateTime;
            query = query.Where(p => p.Status == PostStatus.Published && p.PublishedAt <= now
                && p.Kind == PostKind.Post);
        }
        else
        {
            if (request.Status.HasValue)
                query = query.Where(p => p.Status == request.Status.Value);

            if (request.Kind.HasValue)
                query = query.Where(p => p.Kind == request.Kind.Value);
        }

        if (!string.IsNullOrWhiteSpace(request.TagSlug))
            query = query.Where(p => p.Tags.Any(t => t.Slug == request.TagSlug));

        if (!string.IsNullOrWhiteSpace(request.CategorySlug))
            query = query.Where(p => p.Category != null && p.Category.Slug == request.CategorySlug);

        if (!string.IsNullOrWhiteSpace(request.SearchTerm))
        {
            query = query.Where(p =>
                p.SearchVector.Matches(EF.Functions.PlainToTsQuery("english", request.SearchTerm!)));
        }

        // The public branch sorts by PublishedAt alone (a published post always has one), so the
        // IX_Posts_Published_PublishedAt index can serve the sort; admin must fall back to
        // CreatedAt because drafts have no PublishedAt.
        // The admin Pages list is a placement list, not a timeline: Sidebar, then Footer, then
        // the pages shown nowhere, each by order and title (the order the reader sees them in).
        var ordered = publicOnly
            ? query.OrderByDescending(p => p.PublishedAt)
            : request.Kind == PostKind.Page
                ? query
                    .OrderBy(p => p.NavigationPlacement == NavigationPlacement.Sidebar ? 0
                        : p.NavigationPlacement == NavigationPlacement.Footer ? 1 : 2)
                    .ThenBy(p => p.NavigationOrder)
                    .ThenBy(p => p.Title)
                : query.OrderByDescending(p => p.PublishedAt ?? p.CreatedAt);
        var result = await ToSummaries(ordered, includeAdminCounts).ToPagedResultAsync(request.Page, request.PageSize, ct);
        return result with { Items = [.. result.Items.Select(WithTagsSortedByName)] };
    }

    public async Task<PostDto> CreateAsync(CreatePostRequest request, string authorId, string authorName, CancellationToken ct = default)
    {
        ValidateRequest(request.Title, request.Subtitle, request.MarkdownContent);
        ValidatePageShape(request.Kind, request.CategoryId, request.Tags, request.BannerImageId);
        ValidatePageAnnouncing(request.Kind, request.AnnouncementText, request.AnnounceMastodon, request.AnnounceBluesky);
        var links = await socialLinks.ResolveAsync(request.MastodonUrl, request.BlueskyUrl, ct: ct);

        // For pages a reserved word behaves like a slug another row already holds: the generated
        // slug gets the usual collision suffix. Posts keep their plain generation — they live
        // under /post/{slug} and never shadow a Web route.
        var slug = await slugService.GenerateUniqueSlugAsync(
            request.Title,
            async s => (request.Kind == PostKind.Page && ReservedSlugs.Contains(s))
                || await db.Posts.AnyAsync(p => p.Slug == s, ct));
        ValidatePageSlug(request.Kind, slug);

        // A new post announces on both networks unless the request says otherwise; a page never does.
        var announcementText = NormalizeAnnouncementText(request.AnnouncementText);
        var announceMastodon = request.Kind == PostKind.Post && (request.AnnounceMastodon ?? true);
        var announceBluesky = request.Kind == PostKind.Post && (request.AnnounceBluesky ?? true);
        ValidateAnnouncementText(announcementText, announceMastodon, announceBluesky, slug);

        var tags = await ResolveTagsAsync(request.Tags, ct);
        var category = await ResolveCategoryAsync(request.CategoryId, ct);
        var (summary, readingMinutes) = PostSummaryExtractor.Extract(request.MarkdownContent);

        var now = time.GetUtcNow().UtcDateTime;
        var post = new Post
        {
            Slug = slug,
            Title = request.Title,
            Subtitle = request.Subtitle,
            MarkdownContent = request.MarkdownContent,
            Excerpt = request.Excerpt,
            Summary = summary,
            ReadingMinutes = readingMinutes,
            Status = PostStatus.Draft,
            Kind = request.Kind,
            NavigationPlacement = request.NavigationPlacement,
            NavigationOrder = request.NavigationOrder,
            CreatedAt = now,
            UpdatedAt = now,
            ContentEditedAt = now,
            AuthorId = authorId,
            AuthorName = authorName,
            BannerImageId = request.BannerImageId,
            Tags = tags,
            Category = category,
            AnnouncementText = announcementText,
            AnnounceMastodon = announceMastodon,
            AnnounceBluesky = announceBluesky
        };

        await socialLinks.ApplyAsync(post, links, now, ct);
        db.Posts.Add(post);
        await cacheNotifier.SaveAndNotifyAsync(db, CacheGroup.All, ct);

        return await MapToDtoAsync(post, includeAuthorId: true, ct);
    }

    public async Task<PostDto?> UpdateAsync(Guid id, UpdatePostRequest request, string userId, bool isAdmin, CancellationToken ct = default)
    {
        ValidateRequest(request.Title, request.Subtitle, request.MarkdownContent);

        var post = await db.Posts
            .Include(p => p.Tags)
            .Include(p => p.Category)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        if (post is null)
            return null;

        if (!isAdmin && post.AuthorId != userId)
            return null;

        if (!isAdmin && post.Status != PostStatus.Draft)
            throw new ForbiddenException("Editors can only update draft posts.");

        if (request.Kind != post.Kind)
            throw new ValidationException("Kind cannot be changed after creation.");

        ValidatePageShape(request.Kind, request.CategoryId, request.Tags, request.BannerImageId);
        ValidatePageSlug(post.Kind, post.Slug);
        ValidatePageAnnouncing(post.Kind, request.AnnouncementText, request.AnnounceMastodon, request.AnnounceBluesky);

        // Null leaves the text and the flags as they are; an empty text goes back to the default (stored as null).
        var announcementText = request.AnnouncementText is null ? post.AnnouncementText : NormalizeAnnouncementText(request.AnnouncementText);
        var announceMastodon = request.AnnounceMastodon ?? post.AnnounceMastodon;
        var announceBluesky = request.AnnounceBluesky ?? post.AnnounceBluesky;
        ValidateAnnouncementText(announcementText, announceMastodon, announceBluesky, post.Slug);

        var links = await socialLinks.ResolveAsync(request.MastodonUrl, request.BlueskyUrl, nullMeansUnchanged: true, ct);

        post.Title = request.Title;
        post.Subtitle = request.Subtitle;
        post.MarkdownContent = request.MarkdownContent;
        post.Excerpt = request.Excerpt;
        (post.Summary, post.ReadingMinutes) = PostSummaryExtractor.Extract(request.MarkdownContent);
        post.BannerImageId = request.BannerImageId;
        post.NavigationPlacement = request.NavigationPlacement;
        post.NavigationOrder = request.NavigationOrder;
        post.AnnouncementText = announcementText;
        post.AnnounceMastodon = announceMastodon;
        post.AnnounceBluesky = announceBluesky;
        var now = time.GetUtcNow().UtcDateTime;
        post.UpdatedAt = now;
        post.ContentEditedAt = now;

        post.Tags = await ResolveTagsAsync(request.Tags, ct);
        post.Category = await ResolveCategoryAsync(request.CategoryId, ct);

        // A link that was set, changed or cleared also changes the post's discussion; an edit that leaves the links alone does not
        // touch the cached discussions (CacheGroup.Discussion is not part of All).
        var linksChanged = await socialLinks.ApplyAsync(post, links, now, ct);
        await cacheNotifier.SaveAndNotifyAsync(db, linksChanged ? CacheGroup.All | CacheGroup.Discussion : CacheGroup.All, ct);
        return await MapToDtoAsync(post, includeAuthorId: true, ct);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var post = await db.Posts
            .Include(p => p.MediaAssets)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
        if (post is null)
            return false;

        var mediaFilePaths = post.MediaAssets.Select(a => a.FilePath).ToList();

        db.Posts.Remove(post);
        await cacheNotifier.SaveAndNotifyAsync(db, CacheGroups.PostVisibility, ct);

        foreach (var filePath in mediaFilePaths)
        {
            try
            {
                fileStorage.Delete(filePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogFailedToDeleteMediaFile(logger, ex, filePath, id);
            }
        }

        return true;
    }

    public async Task<PostDto?> PublishAsync(Guid id, PublishRequest request, CancellationToken ct = default)
    {
        var post = await db.Posts
            .Include(p => p.Tags)
            .Include(p => p.Category)
            .Include(p => p.BannerImage)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        if (post is null)
            return null;

        var now = time.GetUtcNow().UtcDateTime;
        var scheduledAt = request.PublishedAt.HasValue ? normalizer.NormalizeToUtc(request.PublishedAt) : now;
        var wasPublished = post.Status == PostStatus.Published;

        if (scheduledAt <= now)
        {
            post.Status = PostStatus.Published;
            post.PublishedAt = scheduledAt;
            ApiMetrics.PostPublishedByEditor();
        }
        else
        {
            post.Status = PostStatus.Scheduled;
            post.PublishedAt = scheduledAt;
        }

        post.UpdatedAt = now;

        // Announcing happens on the transition to Published only (publishing a published post queues nothing), and the rows
        // are saved with the status change in one transaction.
        if (post.Status == PostStatus.Published && !wasPublished)
            await announcements.QueueAsync([post.Id], now, ct);

        await cacheNotifier.SaveAndNotifyAsync(db, CacheGroups.PostVisibility, ct);

        if (post.Status == PostStatus.Scheduled)
        {
            // Scheduled (or rescheduled): the scheduler is sleeping towards some earlier
            // deadline that may no longer be the earliest one, so it must re-read and sleep
            // again — this replica's tick then lands on the new due time.
            scheduledPublishWakeup.Wake();
        }

        return await MapToDtoAsync(post, includeAuthorId: true, ct);
    }

    public async Task<PostDto?> UnpublishAsync(Guid id, CancellationToken ct = default)
    {
        var post = await db.Posts
            .Include(p => p.Tags)
            .Include(p => p.Category)
            .Include(p => p.BannerImage)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        if (post is null)
            return null;

        var wasScheduled = post.Status == PostStatus.Scheduled;
        post.Status = PostStatus.Draft;
        post.PublishedAt = null;
        post.UpdatedAt = time.GetUtcNow().UtcDateTime;
        await announcements.CancelAsync(post.Id, ct);
        await cacheNotifier.SaveAndNotifyAsync(db, CacheGroups.PostVisibility, ct);

        if (wasScheduled)
        {
            // Unscheduled: the deadline the scheduler sleeps towards is gone; let it re-read.
            scheduledPublishWakeup.Wake();
        }

        return await MapToDtoAsync(post, includeAuthorId: true, ct);
    }

    private async Task<ICollection<Tag>> ResolveTagsAsync(IReadOnlyList<string>? tagNames, CancellationToken ct)
    {
        if (tagNames is null || tagNames.Count == 0)
            return [];

        var slugsByName = new List<(string Name, string Slug)>(tagNames.Count);
        var seenSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in tagNames)
        {
            var slug = slugService.GenerateSlug(name);
            if (seenSlugs.Add(slug))
                slugsByName.Add((name, slug));
        }

        var slugs = slugsByName.Select(item => item.Slug).ToList();
        var existing = (await db.Tags.Where(t => slugs.Contains(t.Slug)).ToListAsync(ct))
            .ToDictionary(t => t.Slug);

        return [.. slugsByName.Select(item => existing.TryGetValue(item.Slug, out var tag)
            ? tag
            : new Tag { Name = item.Name, Slug = item.Slug })];
    }

    private async Task<Category?> ResolveCategoryAsync(Guid? categoryId, CancellationToken ct)
    {
        if (categoryId is null)
            return null;

        return await db.Categories.FirstOrDefaultAsync(c => c.Id == categoryId, ct)
            ?? throw new ValidationException("Category not found.");
    }

    private static string GetPublicMediaUrl(Guid id)
    {
        return $"/api/media/{id}";
    }

    /// <summary>
    /// Projects posts to their list summaries in SQL: the markdown body is never loaded and the
    /// admin-only counts are emitted as constants (null) when the caller may not see them.
    /// </summary>
    /// <remarks>
    /// Callers must apply <see cref="WithTagsSortedByName"/> after materialisation — the
    /// SQL-built tag list is unordered.
    /// </remarks>
    internal static IQueryable<PostSummaryDto> ToSummaries(IQueryable<Post> query, bool includeAdminCounts)
    {
        return query.Select(p => new PostSummaryDto(
            p.Id,
            p.Slug,
            p.Title,
            p.Subtitle,
            p.Excerpt,
            p.Summary ?? string.Empty,
            p.ReadingMinutes,
            p.Status,
            p.PublishedAt,
            p.UpdatedAt,
            p.AuthorName,
            p.Category == null
                ? null
                : new CategoryDto(
                    p.Category.Id,
                    p.Category.Name,
                    p.Category.Slug,
                    p.Category.Icon,
                    p.Category.Posts.Count(c => c.Status == PostStatus.Published)),
            p.Tags.Select(t => new TagDto(
                t.Id,
                t.Name,
                t.Slug,
                t.Posts.Count(c => c.Status == PostStatus.Published),
                t.CountsTowardsRelated)).ToList(),
            p.BannerImage == null
                ? null
                : new BannerRefDto(GetPublicMediaUrl(p.BannerImage.Id), p.BannerImage.AltText),
            includeAdminCounts ? (long?)(p.Views == null ? 0 : p.Views.ViewCount) : null,
            includeAdminCounts ? (int?)p.Feedback.Count(f => f.ResolvedAt == null) : null,
            p.Kind,
            p.NavigationPlacement,
            p.NavigationOrder));
    }

    /// <summary>
    /// A post's tags are listed by name, case-insensitive ordinal, in every response. The summary
    /// projection builds its tag list in SQL, where the database collation would decide the
    /// order, so the sort runs here on the materialised DTO instead.
    /// </summary>
    internal static PostSummaryDto WithTagsSortedByName(PostSummaryDto summary) =>
        summary with { Tags = [.. summary.Tags.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)] };

    /// <summary>
    /// <paramref name="includeAuthorId"/>: staff reads and every write response (staff only) carry the
    /// author's IdP subject; public reads leave it null, and the public read cache holds that variant.
    /// </summary>
    private async Task<PostDto> MapToDtoAsync(Post post, bool includeAuthorId, CancellationToken ct)
    {
        var categoryPostCount = post.Category is null
            ? 0
            : await db.Posts.CountAsync(p => p.CategoryId == post.Category.Id && p.Status == PostStatus.Published, ct);

        var tagIds = post.Tags.Select(t => t.Id).ToList();
        var tagPostCounts = await db.Tags
            .AsNoTracking()
            .Where(t => tagIds.Contains(t.Id))
            .Select(t => new { t.Id, Count = t.Posts.Count(p => p.Status == PostStatus.Published) })
            .ToDictionaryAsync(x => x.Id, x => x.Count, ct);

        return new PostDto(
            post.Id,
            post.Slug,
            post.Title,
            post.Subtitle,
            post.MarkdownContent,
            post.Excerpt,
            post.Summary,
            post.Status,
            post.PublishedAt,
            post.CreatedAt,
            post.UpdatedAt,
            includeAuthorId ? post.AuthorId : null,
            post.AuthorName,
            post.BannerImage is null
                ? null
                : new MediaAssetDto(
                    post.BannerImage.Id,
                    post.BannerImage.FileName,
                    post.BannerImage.OriginalName,
                    post.BannerImage.ContentType,
                    GetPublicMediaUrl(post.BannerImage.Id),
                    post.BannerImage.Scope,
                    post.BannerImage.PostId,
                    post.BannerImage.AltText),
            [.. post.Tags
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .Select(t => new TagDto(t.Id, t.Name, t.Slug, tagPostCounts.GetValueOrDefault(t.Id), t.CountsTowardsRelated,
                    includeAuthorId ? t.Hashtags : null))],
            post.Category is null
                ? null
                : new CategoryDto(post.Category.Id, post.Category.Name, post.Category.Slug, post.Category.Icon, categoryPostCount,
                    // Staff only, like the author's id: the editor's announcement previews need them, a reader does not (and the public read cache holds that variant).
                    includeAuthorId ? post.Category.Hashtags : null),
            post.ReadingMinutes,
            post.Kind,
            post.NavigationPlacement,
            post.NavigationOrder,
            post.ContentEditedAt,
            post.MastodonUrl,
            post.BlueskyUrl,
            // Staff only, like the author's id: an anonymous reader has no use for them, and the public read cache holds that variant.
            includeAuthorId ? post.AnnouncementText : null,
            includeAuthorId && post.AnnounceMastodon,
            includeAuthorId && post.AnnounceBluesky);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to delete media file {FilePath} for post {PostId}")]
    private static partial void LogFailedToDeleteMediaFile(ILogger logger, Exception exception, string filePath, Guid postId);
}
