using Croft.Shared.Contracts.Social;

namespace Croft.Api.Social;

/// <summary>What the editor's Discussion tab reads and clicks (brief 12d): the announcements of a post, and "Announce now" / "Retry".</summary>
public interface IAnnouncementService
{
    /// <summary>The post's rows with the readiness of both networks; null for an unknown post.</summary>
    Task<PostAnnouncementsDto?> GetAsync(Guid postId, CancellationToken ct);

    /// <summary>
    /// Sets the post's row for the network (created if absent) to pending now, keeping its attempts (so the sender checks before it sends again).
    /// Null for an unknown post. <see cref="Errors.ValidationException"/> for a page or a network that is not set up;
    /// <see cref="Errors.ConflictException"/> when the post is not published, already has a link for that network, or the row is posted.
    /// </summary>
    Task<AnnouncementStatusDto?> AnnounceAsync(Guid postId, SocialPlatform platform, CancellationToken ct);
}
