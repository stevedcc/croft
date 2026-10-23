namespace Croft.Shared.Contracts.Social;

/// <summary>
/// What the Discussion tab's Announcements block shows: which networks this site can announce on (credentials set) and each network's row, if it has one.
/// <c>PostUrl</c> is the address an announcement carries (empty when the site's address is not set).
/// </summary>
public sealed record PostAnnouncementsDto(
    bool MastodonReady,
    bool BlueskyReady,
    string PostUrl,
    IReadOnlyList<AnnouncementStatusDto> Items);
