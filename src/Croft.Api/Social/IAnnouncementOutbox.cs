namespace Croft.Api.Social;

/// <summary>
/// Puts posts into the announcement outbox and takes them out again (brief 12d). Both methods only change the context; the caller saves, so the rows
/// and the status change that caused them are one transaction.
/// </summary>
public interface IAnnouncementOutbox
{
    /// <summary>
    /// For posts that have just become published: a pending row per network whose flag is on, that is ready, and for which the post has no link yet, unless a row
    /// is already pending, posted or failed. A cancelled row is queued again (its attempts stay). Pages are never queued.
    /// </summary>
    Task QueueAsync(IReadOnlyCollection<Guid> postIds, DateTime now, CancellationToken ct);

    /// <summary>The post was unpublished: its pending rows are cancelled. A row in flight still records its result.</summary>
    Task CancelAsync(Guid postId, CancellationToken ct);
}
