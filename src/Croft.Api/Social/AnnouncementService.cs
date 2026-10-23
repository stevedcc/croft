using Croft.Api.Data;
using Croft.Api.Errors;
using Croft.Api.Models;
using Croft.Shared.Contracts.Social;
using Croft.Shared.Enums;
using Croft.Shared.Social;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Croft.Api.Social;

public sealed class AnnouncementService(BlogDbContext db, IOptions<SocialOptions> options, TimeProvider time) : IAnnouncementService
{
    public async Task<PostAnnouncementsDto?> GetAsync(Guid postId, CancellationToken ct)
    {
        var post = await db.Posts.AsNoTracking().Where(p => p.Id == postId).Select(p => new { p.Slug }).SingleOrDefaultAsync(ct);
        if (post is null)
            return null;

        var settings = options.Value;
        var rows = await db.SocialAnnouncements.AsNoTracking().Where(a => a.PostId == postId).OrderBy(a => a.Platform).ToListAsync(ct);
        return new PostAnnouncementsDto(
            settings.MastodonCanAnnounce,
            settings.BlueskyCanAnnounce,
            string.IsNullOrWhiteSpace(settings.BaseUrl) ? string.Empty : AnnouncementText.PostUrl(settings.BaseUrl, post.Slug),
            [.. rows.Select(ToDto)]);
    }

    public async Task<AnnouncementStatusDto?> AnnounceAsync(Guid postId, SocialPlatform platform, CancellationToken ct)
    {
        var post = await db.Posts.AsNoTracking()
            .Where(p => p.Id == postId)
            .Select(p => new { p.Kind, p.Status, HasLink = platform == SocialPlatform.Mastodon ? p.MastodonUrl != null : p.BlueskyUrl != null })
            .SingleOrDefaultAsync(ct);
        if (post is null)
            return null;

        var settings = options.Value;
        if (post.Kind == PostKind.Page)
            throw new ValidationException("Pages are not announced.");

        var ready = platform == SocialPlatform.Mastodon ? settings.MastodonCanAnnounce : settings.BlueskyCanAnnounce;
        if (!ready)
            throw new ValidationException($"{platform} is not set up on this site.");

        if (post.Status != PostStatus.Published)
            throw new ConflictException("The post is not published.");

        if (post.HasLink)
            throw new ConflictException($"The post already has a {platform} link.");

        var now = time.GetUtcNow().UtcDateTime;
        var row = await db.SocialAnnouncements.SingleOrDefaultAsync(a => a.PostId == postId && a.Platform == platform, ct);
        if (row is null)
        {
            row = new SocialAnnouncement { PostId = postId, Platform = platform };
            db.SocialAnnouncements.Add(row);
        }
        else if (row.State == AnnouncementState.Posted)
        {
            throw new ConflictException($"The post is already announced on {platform}.");
        }

        // A manual retry of a failed announcement starts again: a failed try then waits the first backoff step, not the sixtieth minute.
        // The sender still looks for an earlier send before it sends (the row has been tried before: see Claim.Tried).
        if (row.State == AnnouncementState.Failed)
            row.Attempts = 0;

        row.State = AnnouncementState.Pending;
        row.QueuedAt = now;
        row.NextAttemptAt = now;
        await db.SaveChangesAsync(ct);
        return ToDto(row);
    }

    public static AnnouncementStatusDto ToDto(SocialAnnouncement row) =>
        new(row.Platform, row.State, row.Attempts, row.QueuedAt, row.LastAttemptAt, row.NextAttemptAt, row.LastError, row.RemoteUrl, row.PostedAt);
}
