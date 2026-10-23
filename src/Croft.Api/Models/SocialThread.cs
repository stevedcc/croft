using Croft.Shared.Contracts.Social;

namespace Croft.Api.Models;

/// <summary>
/// The fetch state of one platform's thread of a post (brief 12a): when it last succeeded, when it is
/// next due, and the lease a replica holds while it fetches. A thread exists while the post links to that platform.
/// </summary>
public class SocialThread
{
    public Guid PostId { get; set; }
    public Post Post { get; set; } = null!;
    public SocialPlatform Platform { get; set; }

    public DateTime? LastSuccessAt { get; set; }
    public DateTime? LastAttemptAt { get; set; }

    /// <summary>The last failure in at most 500 characters: a kind and a status, never a stack trace.</summary>
    public string? LastError { get; set; }

    /// <summary>The hash of the stored snapshot: a fetch that yields the same hash changes and invalidates nothing.</summary>
    public string? SnapshotHash { get; set; }

    public DateTime NextDueAt { get; set; }

    /// <summary>Set while a replica fetches this thread; a replica that dies leaves a lease that expires.</summary>
    public DateTime? LeaseUntil { get; set; }
}
