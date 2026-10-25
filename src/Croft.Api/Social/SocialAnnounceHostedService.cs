using Croft.Api.Data;
using Croft.Api.Services;
using Croft.Shared;
using Croft.Shared.Contracts.Social;
using Croft.Shared.Enums;
using Croft.Shared.Social;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Croft.Api.Social;

/// <summary>What one tick did: skipped (social off, nothing ready, or another replica held the job lock) or how many announcements it claimed.</summary>
public sealed record AnnounceTickResult(bool Skipped, int Claimed);

/// <summary>
/// Announces published posts on Mastodon and Bluesky (brief 12d). One loop per API instance, a tick every 30 seconds. A tick <b>claims under the lock, posts outside it</b>,
/// like <see cref="SocialFetchHostedService"/>: inside the job lock it selects up to five due, unleased pending rows of published posts, gives each a five-minute lease and counts
/// the attempt; then, one row at a time and through the platform's gate (so it never overlaps the reply fetcher on one network), it composes the text from the post as it is now, looks for
/// an earlier send when this is not the first attempt, sends, and writes the result in a short transaction. A replica that dies mid-send leaves a lease that runs out, and the next
/// attempt looks before it sends.
/// </summary>
public sealed partial class SocialAnnounceHostedService(
    ILogger<SocialAnnounceHostedService> logger,
    IServiceScopeFactory scopes,
    TimeProvider time,
    ICacheNotifier notifier,
    IOptions<SocialOptions> options,
    IEnumerable<ISocialAnnouncer> announcers,
    SocialPlatformGates gates,
    IFileStorage storage,
    IBlueskyHandleResolver resolver) : BackgroundService
{
    public const int MaxClaimsPerTick = 5;

    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    /// <summary>How long a claim holds a row: only the time a dead replica's claim takes to run out (a request is bounded to 30 s).</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    /// <summary>A row that is still failing this long after it was queued becomes Failed.</summary>
    public static readonly TimeSpan RetryWindow = TimeSpan.FromHours(6);

    /// <summary>The wait after the first, second, ... failed attempt; the last step repeats.</summary>
    public static readonly TimeSpan[] Backoff = [.. new[] { 1, 2, 4, 8, 16, 32, 60 }.Select(minutes => TimeSpan.FromMinutes(minutes))];

    private const int MaxErrorLength = 500;
    private const long MaxBannerBytes = 25 * 1024 * 1024;

    private readonly Dictionary<SocialPlatform, ISocialAnnouncer> _announcers = announcers.ToDictionary(a => a.Platform);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await TickAsync(scope.ServiceProvider.GetRequiredService<BlogDbContext>(), stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogTickFailed(logger, ex.GetType().Name);
            }

            await Task.Delay(TickInterval, time, stoppingToken);
        }
    }

    /// <summary>One tick: claim what is due, post it, record the results. Driven directly by the tests.</summary>
    public async Task<AnnounceTickResult> TickAsync(BlogDbContext db, CancellationToken ct = default)
    {
        var settings = options.Value;
        List<SocialPlatform> platforms = [];
        if (settings.MastodonCanAnnounce)
            platforms.Add(SocialPlatform.Mastodon);
        if (settings.BlueskyCanAnnounce)
            platforms.Add(SocialPlatform.Bluesky);

        // Off (the default) or nothing ready: no query at all. Rows already queued for a network that is not ready wait, untouched.
        if (platforms.Count == 0)
            return new AnnounceTickResult(Skipped: true, Claimed: 0);

        var now = Now();
        List<Claim> claims = [];
        var ran = await BackgroundJobLock.TryRunAsync(db, BackgroundJobIds.SocialAnnounce, token => ClaimAsync(db, platforms, now, claims, token), ct);
        if (!ran)
            return new AnnounceTickResult(Skipped: true, Claimed: 0);

        Dictionary<SocialPlatform, DateTime> busyUntil = [];
        foreach (var claim in claims)
        {
            db.ChangeTracker.Clear();
            ct.ThrowIfCancellationRequested();

            // A network that said "not before then" is not asked again this tick: its remaining claims are given back.
            if (busyUntil.TryGetValue(claim.Platform, out var until))
            {
                await ReleaseAsync(db, claim, until, ct);
                continue;
            }

            if (await ProcessAsync(db, claim, ct) is { } busy)
                busyUntil[claim.Platform] = busy;
        }

        return new AnnounceTickResult(Skipped: false, Claimed: claims.Count);
    }

    /// <summary>Inside the job lock: the oldest due rows get a lease and an attempt (conditional on still being due, so even a broken lock cannot claim a row twice).</summary>
    private static async Task ClaimAsync(BlogDbContext db, List<SocialPlatform> platforms, DateTime now, List<Claim> claims, CancellationToken ct)
    {
        var due = await db.SocialAnnouncements
            .AsNoTracking()
            .Where(a => a.State == AnnouncementState.Pending && a.NextAttemptAt <= now && (a.LeaseUntil == null || a.LeaseUntil < now)
                && platforms.Contains(a.Platform) && a.Post.Status == PostStatus.Published)
            .OrderBy(a => a.NextAttemptAt).ThenBy(a => a.PostId).ThenBy(a => a.Platform)
            .Take(MaxClaimsPerTick)
            .Select(a => new { a.Id, a.PostId, a.Platform, a.Attempts, a.QueuedAt, Tried = a.LastAttemptAt != null })
            .ToListAsync(ct);

        var leaseUntil = now + Lease;
        foreach (var row in due)
        {
            var claimed = await db.SocialAnnouncements
                .Where(a => a.Id == row.Id && a.State == AnnouncementState.Pending && a.NextAttemptAt <= now && (a.LeaseUntil == null || a.LeaseUntil < now))
                .ExecuteUpdateAsync(set => set
                    .SetProperty(a => a.LeaseUntil, (DateTime?)leaseUntil)
                    .SetProperty(a => a.Attempts, a => a.Attempts + 1)
                    .SetProperty(a => a.LastAttemptAt, (DateTime?)now), ct);
            if (claimed == 1)
                claims.Add(new Claim(row.Id, row.PostId, row.Platform, row.Attempts + 1, row.QueuedAt, row.Tried));
        }
    }

    /// <summary>Posts one claimed row and records the outcome; returns the time the network asked to be left alone until, if it did.</summary>
    private async Task<DateTime?> ProcessAsync(BlogDbContext db, Claim claim, CancellationToken ct)
    {
        try
        {
            AnnounceOutcome outcome;
            AnnouncementMessage? sent = null;
            try
            {
                var post = await LoadPostAsync(db, claim, ct);
                if (post is null)
                    return null; // the post is gone and took its rows with it

                var (message, tooLong) = Compose(claim, post);
                if (tooLong is not null)
                {
                    outcome = new AnnounceOutcome.Failed(tooLong);
                }
                else
                {
                    sent = message;
                    outcome = await AnnounceAsync(claim, message!, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A fault nobody expected: recorded by its kind only (a message could carry anything), and the other rows carry on.
                outcome = new AnnounceOutcome.Retry($"Unexpected error ({ex.GetType().Name})", null);
            }

            var status = AnnounceErrors.HttpStatusOf(outcome);
            LogOutcome(logger, claim.Platform, claim.PostId, claim.Attempts, outcome.GetType().Name, status);
            return await RecordAsync(db, claim, outcome, sent, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The result could not be written (the database): the lease runs out and the row is taken again, after a check.
            LogStoreFailed(logger, claim.Platform, claim.PostId, ex.GetType().Name);
            return null;
        }
    }

    private async Task<AnnounceOutcome> AnnounceAsync(Claim claim, AnnouncementMessage message, CancellationToken ct)
    {
        var announcer = _announcers[claim.Platform];

        // Every attempt after the first may follow one that reached the network but never reported back: look before sending.
        if (claim.Attempts > 1 || claim.Tried)
        {
            var earlier = await gates.RunAsync(claim.Platform, () => announcer.FindEarlierAsync(message, ct), ct);
            if (earlier is not AnnounceOutcome.NotFound)
                return earlier;
        }

        return await gates.RunAsync(claim.Platform, () => announcer.SendAsync(message, ct), ct);
    }

    /// <summary>Hashtags is null for a tag that never had any: a projection bypasses the entity's setter and EF hands NULL through.</summary>
    private sealed record TagHashtags(string Name, List<HashtagDto>? Hashtags);

    private sealed record PostFields(
        Guid Id, string Slug, string Title, string? Subtitle, string? AnnouncementText, string? BannerPath,
        List<HashtagDto>? CategoryHashtags, List<TagHashtags> Tags);

    private static Task<PostFields?> LoadPostAsync(BlogDbContext db, Claim claim, CancellationToken ct) =>
        db.Posts.AsNoTracking()
            .Where(p => p.Id == claim.PostId)
            .Select(p => new PostFields(p.Id, p.Slug, p.Title, p.Subtitle, p.AnnouncementText, p.BannerImage == null ? null : p.BannerImage.FilePath,
                p.Category == null ? null : p.Category.Hashtags, p.Tags.Select(t => new TagHashtags(t.Name, t.Hashtags)).ToList()))
            .SingleOrDefaultAsync(ct);

    /// <summary>The message as the post is now (an edit while pending is sent), or the reason a custom text cannot be sent.</summary>
    private (AnnouncementMessage? Message, string? TooLong) Compose(Claim claim, PostFields post)
    {
        var platform = claim.Platform;
        var url = AnnouncementText.PostUrl(options.Value.BaseUrl, post.Slug);
        string body;
        if (post.AnnouncementText is null)
        {
            // The tags in the order the post lists them (PostService sorts by name, ordinal ignoring case).
            var hashtags = AnnouncementText.SelectHashtags(platform, post.CategoryHashtags ?? [],
                post.Tags.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Select(t => (IReadOnlyList<HashtagDto>)(t.Hashtags ?? [])));
            body = AnnouncementText.FitDefault(platform, post.Title, post.Subtitle, hashtags, url);
        }
        else
        {
            body = post.AnnouncementText;
            var length = AnnouncementText.Length(platform, AnnouncementText.Compose(body, url));
            if (length > AnnouncementText.Limit(platform))
                return (null, $"AnnouncementText is too long for {platform} ({length} of {AnnouncementText.Limit(platform)} characters).");
        }

        var banner = post.BannerPath;
        return (new AnnouncementMessage(claim.Id, AnnouncementText.Compose(body, url), url, post.Title, post.Subtitle,
            banner is null || platform != SocialPlatform.Bluesky ? null : _ => Task.FromResult(ReadBanner(banner))), null);
    }

    /// <summary>The banner's bytes, or null when the file is gone, unreadable or enormous: the card then has no picture.</summary>
    private byte[]? ReadBanner(string path)
    {
        try
        {
            using var stream = storage.OpenRead(path);
            if (stream.CanSeek && stream.Length > MaxBannerBytes)
                return null;

            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.Length > MaxBannerBytes ? null : copy.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<DateTime?> RecordAsync(BlogDbContext db, Claim claim, AnnounceOutcome outcome, AnnouncementMessage? sent, CancellationToken ct)
    {
        var now = Now();
        switch (outcome)
        {
            case AnnounceOutcome.Posted posted:
                await RecordPostedAsync(db, claim, posted, sent?.Text, now, ct);
                return null;

            case AnnounceOutcome.Failed failed:
                await RecordFailureAsync(db, claim, failed.Reason, now, ct);
                return null;

            case AnnounceOutcome.Retry retry:
                return await RecordRetryAsync(db, claim, retry, now, ct);

            case AnnounceOutcome.NotFound:
            case null:
                // NotFound can only come from the look for an earlier send, which the caller turns into a send; seeing it here means a bug in an announcer.
                await RecordFailureAsync(db, claim, "Unexpected answer", now, ct);
                return null;
        }
    }

    /// <summary>One transaction: the row is Posted whatever state it is in now (it happened), and the post gets the link unless it got one by hand meanwhile.</summary>
    private async Task RecordPostedAsync(BlogDbContext db, Claim claim, AnnounceOutcome.Posted posted, string? sentText, DateTime now, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var row = await db.SocialAnnouncements.SingleOrDefaultAsync(a => a.Id == claim.Id, ct);
        if (row is null)
        {
            await transaction.RollbackAsync(ct);
            return;
        }

        row.State = AnnouncementState.Posted;
        row.RemoteId = posted.RemoteId;
        row.RemoteUrl = posted.RemoteUrl;
        row.PostedAt = now;
        row.SentText = sentText;
        row.NextAttemptAt = null;
        row.LeaseUntil = null;
        row.LastError = null;

        var post = await db.Posts.SingleOrDefaultAsync(p => p.Id == claim.PostId, ct);
        var linked = false;
        if (post is not null)
        {
            var handLinked = claim.Platform == SocialPlatform.Mastodon ? post.MastodonUrl is not null : post.BlueskyUrl is not null;
            if (!handLinked)
            {
                var links = claim.Platform == SocialPlatform.Mastodon
                    ? new ResolvedSocialLinks(posted.RemoteUrl, posted.RemoteId, null, null, MastodonUnchanged: false, BlueskyUnchanged: true)
                    : new ResolvedSocialLinks(null, null, posted.RemoteUrl, posted.RemoteId, MastodonUnchanged: true, BlueskyUnchanged: false);
                linked = await new SocialLinkService(db, options, resolver).ApplyAsync(post, links, now, ct);
            }
        }

        await db.SaveChangesAsync(ct);
        var changed = linked ? CacheGroup.All | CacheGroup.Discussion : default(CacheGroup);
        if (linked)
            await notifier.NotifyAsync(db, changed, ct);

        await transaction.CommitAsync(ct);
        db.ChangeTracker.Clear();
        if (linked)
            await notifier.InvalidateLocalAsync(changed);
    }

    private static async Task RecordFailureAsync(BlogDbContext db, Claim claim, string reason, DateTime now, CancellationToken ct)
    {
        var text = Cap(reason);
        var updated = await db.SocialAnnouncements
            .Where(a => a.Id == claim.Id && a.State == AnnouncementState.Pending)
            .ExecuteUpdateAsync(set => set
                .SetProperty(a => a.State, AnnouncementState.Failed)
                .SetProperty(a => a.LastError, text)
                .SetProperty(a => a.NextAttemptAt, (DateTime?)null)
                .SetProperty(a => a.LeaseUntil, (DateTime?)null)
                .SetProperty(a => a.LastAttemptAt, (DateTime?)now), ct);
        if (updated == 0)
            await ClearLeaseAsync(db, claim, ct);
    }

    /// <summary>The row is no longer pending (it was unpublished while the post went out): its state stays, but our lease must not linger and delay a later re-queue.</summary>
    private static async Task ClearLeaseAsync(BlogDbContext db, Claim claim, CancellationToken ct) =>
        await db.SocialAnnouncements.Where(a => a.Id == claim.Id).ExecuteUpdateAsync(set => set.SetProperty(a => a.LeaseUntil, (DateTime?)null), ct);

    private static async Task<DateTime?> RecordRetryAsync(BlogDbContext db, Claim claim, AnnounceOutcome.Retry retry, DateTime now, CancellationToken ct)
    {
        var wait = Backoff[Math.Min(claim.Attempts, Backoff.Length) - 1];
        var next = now + wait;
        if (retry.NotBefore is { } notBefore && notBefore > next)
            next = notBefore;

        if (next > claim.QueuedAt + RetryWindow)
        {
            await RecordFailureAsync(db, claim, $"Gave up after 6 hours: {retry.Reason}", now, ct);
            return null;
        }

        var text = Cap(retry.Reason);
        var updated = await db.SocialAnnouncements
            .Where(a => a.Id == claim.Id && a.State == AnnouncementState.Pending)
            .ExecuteUpdateAsync(set => set
                .SetProperty(a => a.LastError, text)
                .SetProperty(a => a.NextAttemptAt, (DateTime?)next)
                .SetProperty(a => a.LeaseUntil, (DateTime?)null)
                .SetProperty(a => a.LastAttemptAt, (DateTime?)now), ct);
        if (updated == 0)
            await ClearLeaseAsync(db, claim, ct);

        return retry.NotBefore;
    }

    /// <summary>A claim given back unsent (the network asked to be left alone): the attempt does not count.</summary>
    private static async Task ReleaseAsync(BlogDbContext db, Claim claim, DateTime until, CancellationToken ct) =>
        await db.SocialAnnouncements
            .Where(a => a.Id == claim.Id && a.State == AnnouncementState.Pending)
            .ExecuteUpdateAsync(set => set
                .SetProperty(a => a.Attempts, a => a.Attempts - 1)
                .SetProperty(a => a.NextAttemptAt, (DateTime?)until)
                .SetProperty(a => a.LeaseUntil, (DateTime?)null), ct);

    private static string Cap(string text) => text.Length > MaxErrorLength ? text[..MaxErrorLength] : text;

    private DateTime Now() => time.GetUtcNow().UtcDateTime;

    /// <summary><c>Tried</c>: the row had an attempt before this claim, even if a manual retry has since set its attempts back to 0.</summary>
    private sealed record Claim(Guid Id, Guid PostId, SocialPlatform Platform, int Attempts, DateTime QueuedAt, bool Tried);

    [LoggerMessage(Level = LogLevel.Error, Message = "The announcement tick failed ({ExceptionType}).")]
    private static partial void LogTickFailed(ILogger logger, string exceptionType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Announcing post {PostId} on {Platform}, attempt {Attempt}: {Outcome}{Status}.")]
    private static partial void LogOutcome(ILogger logger, SocialPlatform platform, Guid postId, int attempt, string outcome, string status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Storing the {Platform} announcement result of post {PostId} failed ({ExceptionType}); its lease will run out.")]
    private static partial void LogStoreFailed(ILogger logger, SocialPlatform platform, Guid postId, string exceptionType);
}
