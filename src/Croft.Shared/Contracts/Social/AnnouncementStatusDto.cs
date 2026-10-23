namespace Croft.Shared.Contracts.Social;

/// <summary>One network's announcement of a post, as the editor's Discussion tab shows it. <c>LastError</c> is a short reason with no secrets in it.</summary>
public sealed record AnnouncementStatusDto(
    SocialPlatform Platform,
    AnnouncementState State,
    int Attempts,
    DateTime QueuedAt,
    DateTime? LastAttemptAt = null,
    DateTime? NextAttemptAt = null,
    string? LastError = null,
    string? RemoteUrl = null,
    DateTime? PostedAt = null);
