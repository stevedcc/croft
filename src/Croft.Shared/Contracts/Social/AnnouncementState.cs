namespace Croft.Shared.Contracts.Social;

/// <summary>Where a post's announcement on one network stands (brief 12d). The numeric values are stored in the database.</summary>
public enum AnnouncementState
{
    /// <summary>Queued: the sender posts it on its next tick, or at <c>NextAttemptAt</c> after a failed try.</summary>
    Pending = 1,

    /// <summary>On the network. Never sent again.</summary>
    Posted = 2,

    /// <summary>Given up: an error that cannot heal, or the retry window ran out. Waits for a click on Retry.</summary>
    Failed = 3,

    /// <summary>The post was unpublished before it was sent.</summary>
    Cancelled = 4,
}
