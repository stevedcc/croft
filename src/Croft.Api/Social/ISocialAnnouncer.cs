using Croft.Shared.Contracts.Social;

namespace Croft.Api.Social;

/// <summary>
/// What one announcement needs to be sent: the outbox row's id (Mastodon's idempotency key), the composed text (body, blank line, address), the address,
/// the post's title and subtitle (Bluesky's link card) and a way to read the banner (only Bluesky uses it, so it is read on demand).
/// </summary>
public sealed record AnnouncementMessage(Guid RowId, string Text, string PostUrl, string Title, string? Subtitle, Func<CancellationToken, Task<byte[]?>>? LoadBanner);

/// <summary>Posts an announcement to one network and looks for an earlier send of it (brief 12d). Neither method logs, and no outcome carries a secret.</summary>
public interface ISocialAnnouncer
{
    SocialPlatform Platform { get; }

    /// <summary>Looks in the account's latest posts for the post's address: <see cref="AnnounceOutcome.Posted"/> when found, <see cref="AnnounceOutcome.NotFound"/> when not, Retry or Failed when the look itself fails.</summary>
    Task<AnnounceOutcome> FindEarlierAsync(AnnouncementMessage message, CancellationToken ct);

    /// <summary>Sends it: <see cref="AnnounceOutcome.Posted"/>, Retry or Failed.</summary>
    Task<AnnounceOutcome> SendAsync(AnnouncementMessage message, CancellationToken ct);
}
