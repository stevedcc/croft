using Croft.Shared.Contracts;
using Croft.Shared.Contracts.Social;

namespace Croft.Web.Services;

public interface IBlogApiClient
{
    Task<PagedResult<PostSummaryDto>> GetPostsAsync(PostListRequest request, CancellationToken ct = default);
    Task<PostDto?> GetPostAsync(string slug, CancellationToken ct = default);

    /// <summary>The discussion below a published post (replies from Mastodon and Bluesky, merged), or null when the post is unknown or not published (brief 12a).</summary>
    Task<PostDiscussionDto?> GetDiscussionAsync(string slug, CancellationToken ct = default);

    /// <summary>One page of tags (name order); <c>q</c> filters names. The suggestion lookups use it too.</summary>
    Task<PagedResult<TagDto>> GetTagsAsync(int page, int pageSize, string? q = null, CancellationToken ct = default);

    /// <summary>The tags with exactly these names (case-insensitive), one call: the editor reads the hashtags of tags it only knows by name.</summary>
    Task<IReadOnlyList<TagDto>> GetTagsByNamesAsync(IReadOnlyList<string> names, CancellationToken ct = default);
    Task<TagDto?> GetTagBySlugAsync(string slug, CancellationToken ct = default);

    /// <summary>One page of categories (name order); <c>q</c> filters names.</summary>
    Task<PagedResult<CategoryDto>> GetCategoriesAsync(int page, int pageSize, string? q = null, CancellationToken ct = default);
    Task<CategoryDto?> GetCategoryBySlugAsync(string slug, CancellationToken ct = default);

    /// <summary>The sidebar's bounded read: at most <paramref name="count"/> categories with published posts, most posts first.</summary>
    Task<IReadOnlyList<CategoryDto>> GetTopCategoriesAsync(int count, CancellationToken ct = default);
    Task<PagedResult<PostSummaryDto>> SearchAsync(string query, int page = 1, int pageSize = 10, CancellationToken ct = default);
    Task<IReadOnlyList<PostSummaryDto>> GetRelatedPostsAsync(string slug, int count = 3, CancellationToken ct = default);

    /// <summary>The public page fetch: the page at /<c>slug</c>, or null when there is none (the page's 404 path).</summary>
    Task<PostDto?> GetPageAsync(string slug, CancellationToken ct = default);

    /// <summary>The shell's page entries, both lists in one call: the published pages placed in the
    /// sidebar and in the footer, each in its order.</summary>
    Task<PageNavigationDto> GetPageNavigationsAsync(CancellationToken ct = default);

    Task<PostDto> CreatePostAsync(CreatePostRequest request, CancellationToken ct = default);
    Task<PostDto> UpdatePostAsync(Guid id, UpdatePostRequest request, CancellationToken ct = default);
    Task<PostDto?> GetPostByIdAsync(Guid id, CancellationToken ct = default);
    Task<PostDto> PublishPostAsync(Guid id, PublishRequest request, CancellationToken ct = default);
    Task<PostDto> UnpublishPostAsync(Guid id, CancellationToken ct = default);
    Task DeletePostAsync(Guid id, CancellationToken ct = default);
    Task<MediaAssetDto> UploadMediaAsync(Stream fileStream, string fileName, string contentType, Guid? postId = null, string scope = "Global", CancellationToken ct = default);
    Task<TagDto> CreateTagAsync(string name, CancellationToken ct = default);
    Task<TagDto> UpdateTagAsync(Guid id, UpdateTagRequest request, CancellationToken ct = default);
    Task DeleteTagAsync(Guid id, CancellationToken ct = default);
    Task<CategoryDto> CreateCategoryAsync(string name, CancellationToken ct = default);
    Task<CategoryDto> UpdateCategoryAsync(Guid id, UpdateCategoryRequest request, CancellationToken ct = default);
    Task DeleteCategoryAsync(Guid id, CancellationToken ct = default);
    /// <summary>One page of the global media, newest first; <c>q</c> filters file names.</summary>
    Task<PagedResult<MediaAssetDto>> GetGlobalMediaAsync(int page, int pageSize, string? q = null, CancellationToken ct = default);

    /// <summary>One page of a post's media, newest first; <c>q</c> filters file names.</summary>
    Task<PagedResult<MediaAssetDto>> GetPostMediaAsync(Guid postId, int page, int pageSize, string? q = null, CancellationToken ct = default);
    Task<MediaAssetDto> UpdateMediaAsync(Guid id, UpdateMediaRequest request, CancellationToken ct = default);
    Task DeleteMediaAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<FeedbackDto>> GetFeedbackAsync(Guid postId, CancellationToken ct = default);
    Task CreateFeedbackAsync(Guid postId, CreateFeedbackRequest request, CancellationToken ct = default);
    Task<FeedbackDto> ResolveFeedbackAsync(Guid postId, Guid feedbackId, CancellationToken ct = default);
    Task<AnalyticsSummaryDto> GetAnalyticsAsync(Guid postId, CancellationToken ct = default);

    /// <summary>Staff read of a post's discussion: the replies the readers see, plus each platform's fetch status (brief 12c). Null when the post is unknown.</summary>
    Task<PostDiscussionAdminDto?> GetDiscussionAdminAsync(Guid postId, CancellationToken ct = default);

    /// <summary>Asks the API to fetch the post's threads at its next tick (202); it does not fetch synchronously.</summary>
    Task RefreshDiscussionAsync(Guid postId, CancellationToken ct = default);

    /// <summary>Staff read of a post's announcements on Mastodon and Bluesky (brief 12d): which networks this site can announce on, and each network's row. Null when the post is unknown.</summary>
    Task<PostAnnouncementsDto?> GetAnnouncementsAsync(Guid postId, CancellationToken ct = default);

    /// <summary>"Announce now" / "Retry" (Admin): queues the post's announcement on one network at once (202). Throws with the API's detail on 400 (a page, a network that is not set up) and 409 (not published, already linked or posted).</summary>
    Task<AnnouncementStatusDto> AnnounceAsync(Guid postId, SocialPlatform platform, CancellationToken ct = default);

    /// <summary>Every block (hidden reply or account), newest first. Admin only on the API.</summary>
    Task<IReadOnlyList<SocialBlockDto>> GetSocialBlocksAsync(CancellationToken ct = default);

    /// <summary>Hides a reply or an account. Idempotent on the API (200 with the existing block, or 201). Evicts this pod's cached reading pages.</summary>
    Task<SocialBlockDto> CreateSocialBlockAsync(CreateSocialBlockRequest request, CancellationToken ct = default);

    /// <summary>Unhides: deletes a block. False when there is no such block (someone else unhid it). Evicts this pod's cached reading pages on success.</summary>
    Task<bool> DeleteSocialBlockAsync(Guid id, CancellationToken ct = default);
}
