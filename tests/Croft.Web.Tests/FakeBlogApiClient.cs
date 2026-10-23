using Croft.Shared.Contracts;
using Croft.Shared.Contracts.Social;
using Croft.Shared.Enums;
using Croft.Web.Services;

namespace Croft.Web.Tests;

/// <summary>
/// Deterministic <see cref="IBlogApiClient"/> double for the Web components: it records the
/// calls and hands back the seeded or synthesised results. Media uploads can be held on
/// <see cref="UploadGate"/> so a test can observe the in-progress state, and deletes can fail on
/// demand with <see cref="DeleteFailure"/> (e.g. the API's banner-use refusal).
/// </summary>
internal sealed class FakeBlogApiClient : IBlogApiClient
{
    public List<MediaAssetDto> GlobalMedia { get; init; } = [];
    public List<MediaAssetDto> PostMedia { get; init; } = [];
    public List<(Guid Id, string? AltText)> AltTextUpdates { get; } = [];
    public List<Guid> DeletedIds { get; } = [];
    public List<(Guid? PostId, string Scope, string FileName)> Uploads { get; } = [];
    public List<MediaAssetDto> UploadedAssets { get; } = [];

    /// <summary>Post mutations in order ("create"/"update"/"publish"/"unpublish"/"resolve").</summary>
    public List<string> Calls { get; } = [];

    public Dictionary<Guid, PostDto> PostsById { get; } = [];
    public List<CreatePostRequest> CreatedPosts { get; } = [];
    public List<(Guid Id, UpdatePostRequest Request)> UpdatedPosts { get; } = [];
    public List<(Guid Id, PublishRequest Request)> PublishedPosts { get; } = [];
    public List<Guid> UnpublishedIds { get; } = [];

    /// <summary>Post summaries served by <see cref="GetPostsAsync"/>, in display order.</summary>
    public List<PostSummaryDto> Posts { get; init; } = [];

    /// <summary>Every list request the caller sent, in order.</summary>
    public List<PostListRequest> PostRequests { get; } = [];

    /// <summary>Posts deleted through <see cref="DeletePostAsync"/>.</summary>
    public List<Guid> DeletedPostIds { get; } = [];

    /// <summary>Set to hold each listing at the API call; the request decides which and when.</summary>
    public Func<PostListRequest, Task>? PostsLoadDelay { get; set; }

    /// <summary>Set to make a listing fail instead of answering. The exception is decided when the
    /// request arrives (before any <see cref="PostsLoadDelay"/> hold) and thrown when it would answer.</summary>
    public Func<PostListRequest, Exception?>? PostsLoadFault { get; set; }
    public List<(Guid PostId, Guid FeedbackId)> ResolvedFeedback { get; } = [];
    public List<CategoryDto> Categories { get; init; } = [];
    public List<TagDto> Tags { get; init; } = [];
    public List<FeedbackDto> Feedback { get; init; } = [];

    /// <summary>Related summaries served by <see cref="GetRelatedPostsAsync"/>.</summary>
    public List<PostSummaryDto> Related { get; init; } = [];

    /// <summary>Pages served by <see cref="GetPageAsync"/>, matched by slug.</summary>
    public List<PostDto> Pages { get; init; } = [];

    /// <summary>The sidebar entries served by <see cref="GetPageNavigationsAsync"/>.</summary>
    public List<PageNavItemDto> PageNavItems { get; init; } = [];

    /// <summary>The footer entries served by <see cref="GetPageNavigationsAsync"/>.</summary>
    public List<PageNavItemDto> FooterNavItems { get; init; } = [];

    /// <summary>Every page slug lookup the caller sent, in order.</summary>
    public List<string> PageSlugRequests { get; } = [];

    /// <summary>How many times the caller read the page navigation (sidebar and footer lists together).</summary>
    public int PageNavRequests { get; private set; }

    /// <summary>Set to hold every upload at the API call; completed by the test.</summary>
    public TaskCompletionSource? UploadGate { get; set; }

    /// <summary>Set to make <see cref="DeleteMediaAsync"/> throw instead of deleting.</summary>
    public Exception? DeleteFailure { get; set; }

    /// <summary>Set to make <see cref="UploadMediaAsync"/> throw instead of uploading.</summary>
    public Exception? UploadFailure { get; set; }

    /// <summary>Set to make the sidebar's top-categories read throw instead of answering.</summary>
    public Exception? TopCategoriesFailure { get; set; }

    /// <summary>Set to make the category listing throw instead of answering.</summary>
    public Exception? CategoriesFailure { get; set; }

    /// <summary>Set to make the tag listing throw instead of answering.</summary>
    public Exception? TagsFailure { get; set; }

    /// <summary>Set to make the tag and category creates throw instead of creating.</summary>
    public Exception? CreateFailure { get; set; }

    /// <summary>Set to make the tag and category deletes throw instead of deleting.</summary>
    public Exception? TagDeleteFailure { get; set; }

    /// <summary>Set to make the single-post read by slug throw instead of answering.</summary>
    public Exception? PostReadFailure { get; set; }

    /// <summary>Set to make the post mutations throw instead of answering.</summary>
    public Exception? PostFailure { get; set; }

    public Guid? PostMediaRequestedFor { get; private set; }

    /// <summary>Set to hold the category listing at the API call; released by the test.</summary>
    public TaskCompletionSource? CategoriesLoadGate { get; set; }

    /// <summary>Every slug lookup the caller sent for a tag, in order.</summary>
    public List<string> TagSlugRequests { get; } = [];

    /// <summary>Every slug lookup the caller sent for a category, in order.</summary>
    public List<string> CategorySlugRequests { get; } = [];

    /// <summary>Every paging request the caller sent for tags, in order (page, pageSize, q).</summary>
    public List<(int Page, int PageSize, string? Q)> TagPageRequests { get; } = [];

    /// <summary>Every UpdateTagAsync call, in order: id and the request it carried.</summary>
    public List<(Guid Id, UpdateTagRequest Request)> TagUpdateRequests { get; } = [];

    /// <summary>Every UpdateCategoryAsync call, in order: id and the request it carried.</summary>
    public List<(Guid Id, UpdateCategoryRequest Request)> CategoryUpdateRequests { get; } = [];

    /// <summary>Every paging request the caller sent for categories, in order (page, pageSize, q).</summary>
    public List<(int Page, int PageSize, string? Q)> CategoryPageRequests { get; } = [];

    /// <summary>Every sidebar top read the caller sent, in order.</summary>
    public List<int> TopCategoryRequests { get; } = [];

    /// <summary>Every paging request the caller sent for the global media, in order.</summary>
    public List<(int Page, int PageSize, string? Q)> GlobalMediaRequests { get; } = [];

    /// <summary>Every paging request the caller sent for a post's media, in order.</summary>
    public List<(Guid PostId, int Page, int PageSize, string? Q)> PostMediaRequests { get; } = [];

    /// <summary>Mirrors the API's paged listing: name-ordered, q filters names, then server paging.
    /// A blank q is recorded as null — the wire sends no q for it, exactly like the real client.</summary>
    public Task<PagedResult<TagDto>> GetTagsAsync(int page, int pageSize, string? q = null, CancellationToken ct = default)
    {
        q = string.IsNullOrWhiteSpace(q) ? null : q;
        TagPageRequests.Add((page, pageSize, q));
        if (TagsFailure is not null)
        {
            throw TagsFailure;
        }

        var matching = Tags.Where(t => Matches(t.Name, q)).OrderBy(t => t.Name);
        return Task.FromResult(Page(matching, page, pageSize));
    }

    public Task<TagDto?> GetTagBySlugAsync(string slug, CancellationToken ct = default)
    {
        TagSlugRequests.Add(slug);
        return Task.FromResult(Tags.FirstOrDefault(t => t.Slug == slug));
    }

    /// <summary>Mirrors the API's paged listing: name-ordered, q filters names, then server paging.
    /// A blank q is recorded as null — the wire sends no q for it, exactly like the real client.</summary>
    public async Task<PagedResult<CategoryDto>> GetCategoriesAsync(int page, int pageSize, string? q = null, CancellationToken ct = default)
    {
        if (CategoriesFailure is not null)
        {
            throw CategoriesFailure;
        }

        q = string.IsNullOrWhiteSpace(q) ? null : q;
        CategoryPageRequests.Add((page, pageSize, q));
        if (CategoriesLoadGate is not null)
        {
            await CategoriesLoadGate.Task;
        }

        var matching = Categories.Where(c => Matches(c.Name, q)).OrderBy(c => c.Name);
        return Page(matching, page, pageSize);
    }

    public Task<CategoryDto?> GetCategoryBySlugAsync(string slug, CancellationToken ct = default)
    {
        CategorySlugRequests.Add(slug);
        return Task.FromResult(Categories.FirstOrDefault(c => c.Slug == slug));
    }

    /// <summary>Mirrors the API's sidebar read: in-use categories, most published posts first.</summary>
    public Task<IReadOnlyList<CategoryDto>> GetTopCategoriesAsync(int count, CancellationToken ct = default)
    {
        if (TopCategoriesFailure is not null)
        {
            throw TopCategoriesFailure;
        }

        TopCategoryRequests.Add(count);
        IReadOnlyList<CategoryDto> top =
            [.. Categories.Where(c => c.PostCount > 0).OrderByDescending(c => c.PostCount).ThenBy(c => c.Name).Take(count)];
        return Task.FromResult(top);
    }

    /// <summary>Mirrors the API's media listing: the seed order stands in for newest first, q filters file names.</summary>
    public Task<PagedResult<MediaAssetDto>> GetGlobalMediaAsync(int page, int pageSize, string? q = null, CancellationToken ct = default)
    {
        q = string.IsNullOrWhiteSpace(q) ? null : q;
        GlobalMediaRequests.Add((page, pageSize, q));
        if (MediaLoadFailure is not null)
        {
            throw MediaLoadFailure;
        }

        var matching = GlobalMedia.Where(a => Matches(a.FileName, q) || Matches(a.OriginalName, q));
        return Task.FromResult(Page(matching, page, pageSize));
    }

    /// <summary>Mirrors the API's per-post listing: the seed order stands in for newest first.</summary>
    public Task<PagedResult<MediaAssetDto>> GetPostMediaAsync(Guid postId, int page, int pageSize, string? q = null, CancellationToken ct = default)
    {
        q = string.IsNullOrWhiteSpace(q) ? null : q;
        PostMediaRequestedFor = postId;
        PostMediaRequests.Add((postId, page, pageSize, q));
        if (MediaLoadFailure is not null)
        {
            throw MediaLoadFailure;
        }

        var matching = PostMedia.Where(a => Matches(a.FileName, q) || Matches(a.OriginalName, q));
        return Task.FromResult(Page(matching, page, pageSize));
    }

    private static bool Matches(string value, string? q) =>
        string.IsNullOrWhiteSpace(q) || value.Contains(q, StringComparison.OrdinalIgnoreCase);

    private static PagedResult<T> Page<T>(IEnumerable<T> source, int page, int pageSize)
    {
        List<T> matching = [.. source];
        return new PagedResult<T>(
            [.. matching.Skip((page - 1) * pageSize).Take(pageSize)], matching.Count, page, pageSize);
    }

    public async Task<MediaAssetDto> UploadMediaAsync(Stream fileStream, string fileName, string contentType, Guid? postId = null, string scope = "Global", CancellationToken ct = default)
    {
        if (UploadGate is not null)
        {
            await UploadGate.Task;
        }

        if (UploadFailure is not null)
        {
            throw UploadFailure;
        }

        Uploads.Add((postId, scope, fileName));
        var id = Guid.CreateVersion7();
        var asset = new MediaAssetDto(id, $"{id:N}", fileName, contentType, $"/api/media/{id}",
            scope == "PostSpecific" ? MediaAssetScope.PostSpecific : MediaAssetScope.Global, postId, null);
        UploadedAssets.Add(asset);
        return asset;
    }

    public Task<MediaAssetDto> UpdateMediaAsync(Guid id, UpdateMediaRequest request, CancellationToken ct = default)
    {
        if (MediaUpdateFailure is not null)
        {
            throw MediaUpdateFailure;
        }

        AltTextUpdates.Add((id, request.AltText));
        var existing = All().FirstOrDefault(a => a.Id == id)
            ?? throw new InvalidOperationException($"No seeded or uploaded asset with id {id}.");
        return Task.FromResult(existing with { AltText = request.AltText });
    }

    /// <summary>Set to make <see cref="UpdateMediaAsync"/> throw instead of saving.</summary>
    public Exception? MediaUpdateFailure { get; set; }

    /// <summary>Set to make the media listings throw instead of answering.</summary>
    public Exception? MediaLoadFailure { get; set; }

    public Task DeleteMediaAsync(Guid id, CancellationToken ct = default)
    {
        if (DeleteFailure is not null)
        {
            throw DeleteFailure;
        }

        DeletedIds.Add(id);
        return Task.CompletedTask;
    }

    private IEnumerable<MediaAssetDto> All() => GlobalMedia.Concat(PostMedia).Concat(UploadedAssets);

    /// <summary>Mirrors the API's listing: the status, kind, tag and category filters, a title substring as the
    /// search term, then server paging. The seed order stands in for the API's ordering.</summary>
    public async Task<PagedResult<PostSummaryDto>> GetPostsAsync(PostListRequest request, CancellationToken ct = default)
    {
        PostRequests.Add(request);
        var fault = PostsLoadFault?.Invoke(request);
        IEnumerable<PostSummaryDto> filtered = Posts;
        if (request.Status is { } status)
        {
            filtered = filtered.Where(p => p.Status == status);
        }

        if (request.Kind is { } kind)
        {
            filtered = filtered.Where(p => p.Kind == kind);
        }

        if (!string.IsNullOrWhiteSpace(request.SearchTerm))
        {
            filtered = filtered.Where(p => p.Title.Contains(request.SearchTerm, StringComparison.OrdinalIgnoreCase));
        }

        // The tag and category pages list one tag's or one category's posts: the API filters by slug
        // and the fake must too, or a page that drops the slug from its request still passes.
        if (!string.IsNullOrWhiteSpace(request.TagSlug))
        {
            filtered = filtered.Where(p => p.Tags.Any(t => t.Slug == request.TagSlug));
        }

        if (!string.IsNullOrWhiteSpace(request.CategorySlug))
        {
            filtered = filtered.Where(p => p.Category?.Slug == request.CategorySlug);
        }

        List<PostSummaryDto> matching = [.. filtered];
        var page = matching.Skip((request.Page - 1) * request.PageSize).Take(request.PageSize);
        var result = new PagedResult<PostSummaryDto>([.. page], matching.Count, request.Page, request.PageSize);
        if (PostsLoadDelay is not null)
        {
            // The rows are captured before the hold, so a released stale call answers with the
            // listing as it was when it started.
            await PostsLoadDelay(request);
        }

        if (fault is not null)
        {
            throw fault;
        }

        return result;
    }
    /// <summary>Every post slug lookup the caller sent, in order.</summary>
    public List<string> PostSlugRequests { get; } = [];

    /// <summary>Every related-posts request the caller sent, in order (slug, count).</summary>
    public List<(string Slug, int Count)> RelatedPostsRequests { get; } = [];

    public Task<PostDto?> GetPostAsync(string slug, CancellationToken ct = default)
    {
        if (PostReadFailure is not null)
        {
            throw PostReadFailure;
        }

        PostSlugRequests.Add(slug);
        return Task.FromResult(PostsById.Values.FirstOrDefault(p => p.Slug == slug));
    }
    public Task<PostDto?> GetPageAsync(string slug, CancellationToken ct = default)
    {
        PageSlugRequests.Add(slug);
        return Task.FromResult(Pages.FirstOrDefault(p => p.Slug == slug));
    }
    public Task<PageNavigationDto> GetPageNavigationsAsync(CancellationToken ct = default)
    {
        PageNavRequests++;
        return Task.FromResult(new PageNavigationDto([.. PageNavItems], [.. FooterNavItems]));
    }
    public async Task<PagedResult<PostSummaryDto>> SearchAsync(string query, int page = 1, int pageSize = 10, CancellationToken ct = default)
    {
        SearchRequests.Add(query);
        IEnumerable<PostSummaryDto> filtered = Posts.Where(p => p.Title.Contains(query, StringComparison.OrdinalIgnoreCase));
        List<PostSummaryDto> matching = [.. filtered];
        var result = new PagedResult<PostSummaryDto>(
            [.. matching.Skip((page - 1) * pageSize).Take(pageSize)], matching.Count, page, pageSize);
        if (SearchLoadDelay is not null)
        {
            // The rows are captured before the hold, like GetPostsAsync.
            await SearchLoadDelay(query);
        }

        return result;
    }

    /// <summary>Every search query the caller sent, in order.</summary>
    public List<string> SearchRequests { get; } = [];

    /// <summary>Set to hold each search at the API call; the query decides which and when.</summary>
    public Func<string, Task>? SearchLoadDelay { get; set; }
    /// <summary>Discussions served by <see cref="GetDiscussionAsync"/>, by slug; a slug with none is "not found" (null).</summary>
    public Dictionary<string, PostDiscussionDto> Discussions { get; } = [];

    public List<string> DiscussionRequests { get; } = [];

    /// <summary>Set to make <see cref="GetDiscussionAsync"/> fail (after the request is recorded and the gate has opened).</summary>
    public Exception? DiscussionFailure { get; set; }

    /// <summary>Set to hold <see cref="GetDiscussionAsync"/> (and the related read, <see cref="RelatedGate"/>) at the API call until the task completes.</summary>
    public Task? DiscussionGate { get; set; }

    public Task? RelatedGate { get; set; }

    public async Task<PostDiscussionDto?> GetDiscussionAsync(string slug, CancellationToken ct = default)
    {
        DiscussionRequests.Add(slug);
        if (DiscussionGate is not null)
            await DiscussionGate;
        if (DiscussionFailure is not null)
            throw DiscussionFailure;
        return Discussions.GetValueOrDefault(slug);
    }

    public async Task<IReadOnlyList<PostSummaryDto>> GetRelatedPostsAsync(string slug, int count = 3, CancellationToken ct = default)
    {
        RelatedPostsRequests.Add((slug, count));
        if (RelatedGate is not null)
            await RelatedGate;
        return [.. Related.Take(count)];
    }
    public Task<PostDto> CreatePostAsync(CreatePostRequest request, CancellationToken ct = default)
    {
        if (PostFailure is not null)
        {
            throw PostFailure;
        }

        Calls.Add("create");
        CreatedPosts.Add(request);
        var id = Guid.CreateVersion7();
        var post = Draft(id, request.Title, request.MarkdownContent) with
        {
            Subtitle = request.Subtitle,
            Excerpt = request.Excerpt,
            Tags = [.. (request.Tags ?? []).Select(name => new TagDto(Guid.CreateVersion7(), name, name, 0, true))],
            Category = Categories.FirstOrDefault(c => c.Id == request.CategoryId),
            BannerImage = UploadedAssets.FirstOrDefault(a => a.Id == request.BannerImageId),
            Kind = request.Kind,
            NavigationPlacement = request.NavigationPlacement,
            NavigationOrder = request.NavigationOrder,
            MastodonUrl = request.MastodonUrl,
            BlueskyUrl = request.BlueskyUrl,
            // Like the API: the flags left out are on for a post and off for a page.
            AnnouncementText = string.IsNullOrWhiteSpace(request.AnnouncementText) ? null : request.AnnouncementText,
            AnnounceMastodon = request.Kind == PostKind.Post && (request.AnnounceMastodon ?? true),
            AnnounceBluesky = request.Kind == PostKind.Post && (request.AnnounceBluesky ?? true),
        };
        PostsById[id] = post;
        return Task.FromResult(post);
    }

    public Task<PostDto> UpdatePostAsync(Guid id, UpdatePostRequest request, CancellationToken ct = default)
    {
        if (PostFailure is not null)
        {
            throw PostFailure;
        }

        Calls.Add("update");
        UpdatedPosts.Add((id, request));
        if (request.Kind != PostsById[id].Kind)
        {
            // Mirrors the API: Kind is fixed after creation (the request must carry the old one).
            throw new HttpRequestException("Kind cannot be changed after creation.");
        }

        var post = PostsById[id] with
        {
            Title = request.Title,
            Subtitle = request.Subtitle,
            Excerpt = request.Excerpt,
            MarkdownContent = request.MarkdownContent,
            Tags = [.. (request.Tags ?? []).Select(name => new TagDto(Guid.CreateVersion7(), name, name, 0, true))],
            Category = Categories.FirstOrDefault(c => c.Id == request.CategoryId),
            BannerImage = UploadedAssets.FirstOrDefault(a => a.Id == request.BannerImageId),
            NavigationPlacement = request.NavigationPlacement,
            NavigationOrder = request.NavigationOrder,
            // Like the API: null leaves the link, "" clears it, anything else sets it.
            MastodonUrl = request.MastodonUrl is null ? PostsById[id].MastodonUrl : request.MastodonUrl.Length == 0 ? null : request.MastodonUrl,
            BlueskyUrl = request.BlueskyUrl is null ? PostsById[id].BlueskyUrl : request.BlueskyUrl.Length == 0 ? null : request.BlueskyUrl,
            // Like the API: null leaves the text and the flags, an empty text goes back to the default.
            AnnouncementText = request.AnnouncementText is null ? PostsById[id].AnnouncementText : string.IsNullOrWhiteSpace(request.AnnouncementText) ? null : request.AnnouncementText,
            AnnounceMastodon = request.AnnounceMastodon ?? PostsById[id].AnnounceMastodon,
            AnnounceBluesky = request.AnnounceBluesky ?? PostsById[id].AnnounceBluesky,
        };
        PostsById[id] = post;
        return Task.FromResult(post);
    }

    public Task<PostDto> PublishPostAsync(Guid id, PublishRequest request, CancellationToken ct = default)
    {
        Calls.Add("publish");
        PublishedPosts.Add((id, request));
        // The API's response is authoritative for the resulting status (its clock decides
        // Scheduled vs Published at the moment of the request); tests can dictate it.
        var post = PostsById[id] with
        {
            PublishedAt = request.PublishedAt ?? PostsById[id].PublishedAt ?? DateTime.UtcNow,
            Status = PublishStatus?.Invoke(id, request) ?? PostStatus.Published,
        };
        PostsById[id] = post;
        return Task.FromResult(post);
    }

    /// <summary>The status <see cref="PublishPostAsync"/> reports back; null reports Published.</summary>
    public Func<Guid, PublishRequest, PostStatus>? PublishStatus { get; set; }
    /// <summary>Mirrors the API's create: idempotent by slug — an existing name comes back as-is.</summary>
    public Task<TagDto> CreateTagAsync(string name, CancellationToken ct = default)
    {
        if (CreateFailure is not null)
        {
            throw CreateFailure;
        }

        var tag = Tags.FirstOrDefault(t => t.Name == name)
            ?? new TagDto(Guid.CreateVersion7(), name, name.ToLowerInvariant(), 0, true);
        if (!Tags.Contains(tag))
        {
            Tags.Add(tag);
        }

        return Task.FromResult(tag);
    }
    public Task<TagDto> UpdateTagAsync(Guid id, UpdateTagRequest request, CancellationToken ct = default)
    {
        TagUpdateRequests.Add((id, request));
        if (UpdateFailure is not null)
        {
            throw UpdateFailure;
        }

        var index = Tags.FindIndex(t => t.Id == id);
        var tag = Tags[index] with
        {
            Name = request.Name,
            CountsTowardsRelated = request.CountsTowardsRelated ?? Tags[index].CountsTowardsRelated,
            Hashtags = request.Hashtags ?? Tags[index].Hashtags,
        };
        Tags[index] = tag;
        return Task.FromResult(tag);
    }
    public Task DeleteTagAsync(Guid id, CancellationToken ct = default) =>
        throw TagDeleteFailure ?? new NotImplementedException();
    /// <summary>Mirrors the API's create: idempotent by slug — an existing name comes back as-is.</summary>
    public Task<CategoryDto> CreateCategoryAsync(string name, CancellationToken ct = default)
    {
        if (CreateFailure is not null)
        {
            throw CreateFailure;
        }

        var category = Categories.FirstOrDefault(c => c.Name == name)
            ?? new CategoryDto(Guid.CreateVersion7(), name, name.ToLowerInvariant(), null, 0);
        if (!Categories.Contains(category))
        {
            Categories.Add(category);
        }

        return Task.FromResult(category);
    }
    public Task<CategoryDto> UpdateCategoryAsync(Guid id, UpdateCategoryRequest request, CancellationToken ct = default)
    {
        CategoryUpdateRequests.Add((id, request));
        if (UpdateFailure is not null)
        {
            throw UpdateFailure;
        }

        var index = Categories.FindIndex(c => c.Id == id);
        var category = Categories[index] with { Name = request.Name, Icon = request.Icon, Hashtags = request.Hashtags ?? Categories[index].Hashtags };
        Categories[index] = category;
        return Task.FromResult(category);
    }

    /// <summary>Set to make the in-place tag/category updates throw instead of saving.</summary>
    public Exception? UpdateFailure { get; set; }

    public Task DeleteCategoryAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<FeedbackDto>> GetFeedbackAsync(Guid postId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<FeedbackDto>>([.. Feedback.Where(f => f.PostId == postId)]);

    /// <summary>Feedback created through <see cref="CreateFeedbackAsync"/>, in order.</summary>
    public List<(Guid PostId, CreateFeedbackRequest Request)> CreatedFeedback { get; } = [];

    public Task CreateFeedbackAsync(Guid postId, CreateFeedbackRequest request, CancellationToken ct = default)
    {
        CreatedFeedback.Add((postId, request));
        Feedback.Add(new FeedbackDto(Guid.CreateVersion7(), postId, "editor-id", "Ed",
            request.QuotedText, request.Comment, new DateTime(2026, 10, 7, 9, 0, 0, DateTimeKind.Utc), null));
        return Task.CompletedTask;
    }

    public Task<FeedbackDto> ResolveFeedbackAsync(Guid postId, Guid feedbackId, CancellationToken ct = default)
    {
        Calls.Add("resolve");
        ResolvedFeedback.Add((postId, feedbackId));
        var resolved = Feedback.Single(f => f.Id == feedbackId) with
        {
            ResolvedAt = new DateTime(2026, 10, 6, 12, 5, 0, DateTimeKind.Utc),
        };
        return Task.FromResult(resolved);
    }

    /// <summary>Set to hold each single-post read at the API call; released by the test.</summary>
    public TaskCompletionSource? PostByIdGate { get; set; }

    /// <summary>Set to make each single-post read fail with it.</summary>
    public Exception? PostByIdFailure { get; set; }

    public async Task<PostDto?> GetPostByIdAsync(Guid id, CancellationToken ct = default)
    {
        if (PostByIdFailure is not null)
        {
            throw PostByIdFailure;
        }

        if (PostByIdGate is not null)
        {
            await PostByIdGate.Task;
        }

        return PostsById.GetValueOrDefault(id);
    }

    public Task<PostDto> UnpublishPostAsync(Guid id, CancellationToken ct = default)
    {
        Calls.Add("unpublish");
        UnpublishedIds.Add(id);
        var post = PostsById[id] with { Status = PostStatus.Draft, PublishedAt = null };
        PostsById[id] = post;
        return Task.FromResult(post);
    }

    public Task DeletePostAsync(Guid id, CancellationToken ct = default)
    {
        if (PostFailure is not null)
        {
            throw PostFailure;
        }

        Calls.Add("delete");
        DeletedPostIds.Add(id);
        Posts.RemoveAll(p => p.Id == id);
        return Task.CompletedTask;
    }

    /// <summary>Analytics served by <see cref="GetAnalyticsAsync"/>; unseeded calls still fail loudly.</summary>
    public AnalyticsSummaryDto? Analytics { get; init; }

    /// <summary>Set to make <see cref="GetAnalyticsAsync"/> throw instead of answering.</summary>
    public Exception? AnalyticsFailure { get; set; }

    public Task<AnalyticsSummaryDto> GetAnalyticsAsync(Guid postId, CancellationToken ct = default) =>
        AnalyticsFailure is not null
            ? Task.FromException<AnalyticsSummaryDto>(AnalyticsFailure)
            : Task.FromResult(Analytics ?? throw new NotImplementedException());

    /// <summary>A draft post shell the mutation methods build their results from.</summary>
    private static PostDto Draft(Guid id, string title, string content) => new(
        id, title.ToLowerInvariant().Replace(' ', '-'), title, null, content, null, null,
        PostStatus.Draft, null, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), "author-id", "Author", null, [], null, 1);

    // Brief 12c: the staff discussion reads and the blocks.

    /// <summary>Admin reads served by <see cref="GetDiscussionAdminAsync"/>, by post id; an id with none is "not found" (null).</summary>
    public Dictionary<Guid, PostDiscussionAdminDto> DiscussionAdmins { get; } = [];

    public List<Guid> DiscussionAdminRequests { get; } = [];

    /// <summary>Set to make <see cref="GetDiscussionAdminAsync"/> fail.</summary>
    public Exception? DiscussionAdminFailure { get; set; }

    public List<Guid> RefreshedDiscussions { get; } = [];

    /// <summary>Set to make <see cref="RefreshDiscussionAsync"/> fail.</summary>
    public Exception? RefreshFailure { get; set; }

    /// <summary>Set to hold <see cref="RefreshDiscussionAsync"/> until the task completes.</summary>
    public Task? RefreshGate { get; set; }

    // Brief 12d: a post's announcements.

    /// <summary>Announcements served by <see cref="GetAnnouncementsAsync"/>, by post id; an id with none is "not found" (null).</summary>
    public Dictionary<Guid, PostAnnouncementsDto> Announcements { get; } = [];

    public List<Guid> AnnouncementRequests { get; } = [];

    /// <summary>Set to make <see cref="GetAnnouncementsAsync"/> fail.</summary>
    public Exception? AnnouncementsFailure { get; set; }

    public List<(Guid PostId, SocialPlatform Platform)> Announced { get; } = [];

    /// <summary>Set to make <see cref="AnnounceAsync"/> fail (after the call is recorded).</summary>
    public Exception? AnnounceFailure { get; set; }

    /// <summary>What <see cref="AnnounceAsync"/> does to the served announcements: by default the platform's row becomes Pending with no error.</summary>
    public Action<Guid, SocialPlatform>? OnAnnounce { get; set; }

    public Task<PostAnnouncementsDto?> GetAnnouncementsAsync(Guid postId, CancellationToken ct = default)
    {
        AnnouncementRequests.Add(postId);
        if (AnnouncementsFailure is not null)
            throw AnnouncementsFailure;
        return Task.FromResult(Announcements.GetValueOrDefault(postId));
    }

    public Task<AnnouncementStatusDto> AnnounceAsync(Guid postId, SocialPlatform platform, CancellationToken ct = default)
    {
        Announced.Add((postId, platform));
        if (AnnounceFailure is not null)
            throw AnnounceFailure;

        var row = new AnnouncementStatusDto(platform, AnnouncementState.Pending, 0, DateTime.UtcNow, NextAttemptAt: DateTime.UtcNow);
        if (OnAnnounce is not null)
        {
            OnAnnounce(postId, platform);
        }
        else if (Announcements.TryGetValue(postId, out var current))
        {
            Announcements[postId] = current with { Items = [.. current.Items.Where(i => i.Platform != platform), row] };
        }

        return Task.FromResult(row);
    }

    /// <summary>The blocks <see cref="GetSocialBlocksAsync"/> serves (newest first, as the API does) and <see cref="CreateSocialBlockAsync"/> adds to.</summary>
    public List<SocialBlockDto> Blocks { get; } = [];

    public List<CreateSocialBlockRequest> CreatedBlocks { get; } = [];

    public List<Guid> DeletedBlockIds { get; } = [];

    /// <summary>Set to make <see cref="CreateSocialBlockAsync"/> fail.</summary>
    public Exception? CreateBlockFailure { get; set; }

    /// <summary>Set to make <see cref="DeleteSocialBlockAsync"/> fail.</summary>
    public Exception? DeleteBlockFailure { get; set; }

    /// <summary>When true, <see cref="DeleteSocialBlockAsync"/> answers "no such block" (false) without removing anything: someone else unhid it first.</summary>
    public bool DeleteBlockIsAlreadyGone { get; set; }

    public Task<PostDiscussionAdminDto?> GetDiscussionAdminAsync(Guid postId, CancellationToken ct = default)
    {
        DiscussionAdminRequests.Add(postId);
        if (DiscussionAdminFailure is not null)
            throw DiscussionAdminFailure;
        return Task.FromResult(DiscussionAdmins.GetValueOrDefault(postId));
    }

    public async Task RefreshDiscussionAsync(Guid postId, CancellationToken ct = default)
    {
        RefreshedDiscussions.Add(postId);
        if (RefreshGate is not null)
            await RefreshGate;
        if (RefreshFailure is not null)
            throw RefreshFailure;
    }

    /// <summary>Set to make <see cref="GetSocialBlocksAsync"/> fail.</summary>
    public Exception? BlocksFailure { get; set; }

    /// <summary>How many times <see cref="GetSocialBlocksAsync"/> was called.</summary>
    public int BlockReads { get; private set; }

    public Task<IReadOnlyList<SocialBlockDto>> GetSocialBlocksAsync(CancellationToken ct = default)
    {
        BlockReads++;
        if (BlocksFailure is not null)
            throw BlocksFailure;
        return Task.FromResult<IReadOnlyList<SocialBlockDto>>([.. Blocks]);
    }

    /// <summary>Like the API: the block is stored and the matching replies (with their subtrees) leave every admin read.</summary>
    public Task<SocialBlockDto> CreateSocialBlockAsync(CreateSocialBlockRequest request, CancellationToken ct = default)
    {
        CreatedBlocks.Add(request);
        if (CreateBlockFailure is not null)
            throw CreateBlockFailure;
        var block = new SocialBlockDto(Guid.CreateVersion7(), request.Platform, request.Kind, request.ExternalId,
            request.Label, request.Note, "Admin", DateTimeOffset.UtcNow);
        Blocks.Insert(0, block);
        foreach (var (id, admin) in DiscussionAdmins.ToList())
        {
            var replies = WithoutBlocked(admin.Discussion.Replies, request);
            DiscussionAdmins[id] = admin with
            {
                Discussion = admin.Discussion with { Replies = replies, ReplyCount = Count(replies) },
                Threads = [.. admin.Threads.Select(t => t.Platform == request.Platform ? t with { StoredReplies = CountOf(replies, t.Platform) } : t)],
            };
        }

        return Task.FromResult(block);

        static List<DiscussionReplyDto> WithoutBlocked(IEnumerable<DiscussionReplyDto> replies, CreateSocialBlockRequest request) =>
        [
            .. replies
                .Where(r => !(r.Platform == request.Platform
                    && (request.Kind == SocialBlockKind.Reply ? r.ExternalId : r.AuthorAccountId) == request.ExternalId))
                .Select(r => r with { Replies = WithoutBlocked(r.Replies, request) }),
        ];
        static int Count(IEnumerable<DiscussionReplyDto> replies) => replies.Sum(r => 1 + Count(r.Replies));
        static int CountOf(IEnumerable<DiscussionReplyDto> replies, SocialPlatform platform) =>
            replies.Sum(r => (r.Platform == platform ? 1 : 0) + CountOf(r.Replies, platform));
    }

    public Task<bool> DeleteSocialBlockAsync(Guid id, CancellationToken ct = default)
    {
        DeletedBlockIds.Add(id);
        if (DeleteBlockFailure is not null)
            throw DeleteBlockFailure;
        if (DeleteBlockIsAlreadyGone)
        {
            Blocks.RemoveAll(b => b.Id == id);
            return Task.FromResult(false);
        }

        return Task.FromResult(Blocks.RemoveAll(b => b.Id == id) > 0);
    }
}
