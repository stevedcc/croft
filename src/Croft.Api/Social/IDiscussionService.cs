using Croft.Shared.Contracts.Social;

namespace Croft.Api.Social;

/// <summary>A block was created now (<c>Created</c>) or already existed (the same platform, kind and id).</summary>
public sealed record SocialBlockResult(SocialBlockDto Block, bool Created);

/// <summary>
/// A post's discussion and its moderation (brief 12a): the public read (cached for anonymous callers), the staff read, the refresh trigger and the blocklist.
/// </summary>
public interface IDiscussionService
{
    /// <summary>The discussion of the published post behind <paramref name="slug"/>; null when the post is unknown or not published.</summary>
    Task<PostDiscussionDto?> GetPublicAsync(string slug, CancellationToken ct = default);

    /// <summary>Every stored reply and each thread's status (never cached); null when the post does not exist.</summary>
    Task<PostDiscussionAdminDto?> GetAdminAsync(Guid postId, CancellationToken ct = default);

    /// <summary>Marks the post's threads due now; false when the post does not exist.</summary>
    Task<bool> RefreshAsync(Guid postId, CancellationToken ct = default);

    Task<IReadOnlyList<SocialBlockDto>> ListBlocksAsync(CancellationToken ct = default);

    /// <summary>Hides a reply or an account: stored replies (and their subtrees) go at once, on every post, and the discussions are invalidated on every replica.</summary>
    Task<SocialBlockResult> CreateBlockAsync(CreateSocialBlockRequest request, string createdBy, CancellationToken ct = default);

    /// <summary>Removes a block and makes the platform's threads due now (the replies come back on the next fetch); false when there is no such block.</summary>
    Task<bool> DeleteBlockAsync(Guid id, CancellationToken ct = default);
}
