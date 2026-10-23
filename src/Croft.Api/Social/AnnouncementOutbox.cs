using Croft.Api.Data;
using Croft.Api.Models;
using Croft.Shared.Contracts.Social;
using Croft.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Croft.Api.Social;

/// <inheritdoc />
public sealed class AnnouncementOutbox(BlogDbContext db, IOptions<SocialOptions> options) : IAnnouncementOutbox
{
    public async Task QueueAsync(IReadOnlyCollection<Guid> postIds, DateTime now, CancellationToken ct)
    {
        var settings = options.Value;
        if (postIds.Count == 0 || !(settings.MastodonCanAnnounce || settings.BlueskyCanAnnounce))
            return;

        var posts = await db.Posts
            .AsNoTracking()
            .Where(p => postIds.Contains(p.Id) && p.Kind == PostKind.Post)
            .Select(p => new { p.Id, p.AnnounceMastodon, p.AnnounceBluesky, HasMastodonLink = p.MastodonUrl != null, HasBlueskyLink = p.BlueskyUrl != null })
            .ToListAsync(ct);
        if (posts.Count == 0)
            return;

        var ids = posts.Select(p => p.Id).ToList();
        var rows = await db.SocialAnnouncements.Where(a => ids.Contains(a.PostId)).ToListAsync(ct);
        foreach (var post in posts)
        {
            if (settings.MastodonCanAnnounce && post.AnnounceMastodon && !post.HasMastodonLink)
                Queue(rows, post.Id, SocialPlatform.Mastodon, now);

            if (settings.BlueskyCanAnnounce && post.AnnounceBluesky && !post.HasBlueskyLink)
                Queue(rows, post.Id, SocialPlatform.Bluesky, now);
        }
    }

    private void Queue(List<SocialAnnouncement> rows, Guid postId, SocialPlatform platform, DateTime now)
    {
        var row = rows.FirstOrDefault(a => a.PostId == postId && a.Platform == platform);
        if (row is null)
        {
            db.SocialAnnouncements.Add(new SocialAnnouncement
            {
                PostId = postId,
                Platform = platform,
                State = AnnouncementState.Pending,
                QueuedAt = now,
                NextAttemptAt = now,
            });
            return;
        }

        // Pending is queued already, Posted has happened, Failed waits for the click on Retry; only a cancelled row comes back.
        if (row.State != AnnouncementState.Cancelled)
            return;

        row.State = AnnouncementState.Pending;
        row.QueuedAt = now;
        row.NextAttemptAt = now;
    }

    public async Task CancelAsync(Guid postId, CancellationToken ct)
    {
        foreach (var row in await db.SocialAnnouncements.Where(a => a.PostId == postId && a.State == AnnouncementState.Pending).ToListAsync(ct))
        {
            row.State = AnnouncementState.Cancelled;
            row.NextAttemptAt = null;
        }
    }
}
