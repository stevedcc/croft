using Croft.Shared.Contracts.Social;

namespace Croft.Api.Models;

/// <summary>
/// The outbox row of one post's announcement on one network (brief 12d); unique per post and platform. Its <see cref="Id"/> is Mastodon's
/// <c>Idempotency-Key</c>. A <see cref="AnnouncementState.Posted"/> row is never sent again.
/// </summary>
public class SocialAnnouncement
{
    public Guid Id { get; set; }
    public Guid PostId { get; set; }
    public Post Post { get; set; } = null!;
    public SocialPlatform Platform { get; set; }
    public AnnouncementState State { get; set; }

    /// <summary>Claims so far: the sender counts one when it claims the row, before it sends.</summary>
    public int Attempts { get; set; }

    /// <summary>When the row was queued (or retried by hand): the 6-hour retry window starts here.</summary>
    public DateTime QueuedAt { get; set; }

    /// <summary>When the sender may claim the row; null unless <see cref="State"/> is Pending.</summary>
    public DateTime? NextAttemptAt { get; set; }

    /// <summary>Set while a replica works on the row; a replica that dies leaves a lease that runs out.</summary>
    public DateTime? LeaseUntil { get; set; }

    public DateTime? LastAttemptAt { get; set; }

    /// <summary>The last failure in at most 500 characters: a kind and a status, never a secret or a response body.</summary>
    public string? LastError { get; set; }

    /// <summary>The network's id of the announcement (status id / at:// URI).</summary>
    public string? RemoteId { get; set; }

    /// <summary>Where the announcement can be read on the network.</summary>
    public string? RemoteUrl { get; set; }

    public DateTime? PostedAt { get; set; }

    /// <summary>The text that was sent, as sent (for the record).</summary>
    public string? SentText { get; set; }
}
