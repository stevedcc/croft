using Croft.Api.Data;
using Croft.Api.Models;
using Croft.Api.Services;
using Croft.Shared;
using Croft.Shared.Contracts.Social;
using Croft.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.Options;

namespace Croft.Api.Social;

/// <summary>What one tick did: skipped (social off, or another replica held the job lock) or how many threads it claimed.</summary>
public sealed record SocialTickResult(bool Skipped, int Claimed);

/// <summary>
/// Fetches the replies to the posts' linked announcements (brief 12a). One loop per API instance, a tick every minute. A tick
/// <b>claims under the lock, fetches outside it</b>: inside the job lock it selects the threads that are due (of published posts) and gives
/// each a five-minute lease, commits, and only then asks the platforms, one request at a time per platform, writing each result in a short
/// transaction of its own. A replica that dies mid-fetch leaves a lease that runs out; two replicas never fetch one thread twice.
/// </summary>
public sealed partial class SocialFetchHostedService(
    ILogger<SocialFetchHostedService> logger,
    IServiceScopeFactory scopes,
    TimeProvider time,
    ICacheNotifier notifier,
    IOptions<SocialOptions> options,
    IEnumerable<ISocialThreadSource> sources,
    SocialPlatformGates gates,
    ISocialAvatars avatars) : BackgroundService
{
    /// <summary>The most threads one tick claims; the rest are due all along and wait for the next tick.</summary>
    public const int MaxClaimsPerTick = 20;

    public static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    /// <summary>How long a claim holds a thread. The fetch itself is bounded (10 s per request), so this is only the time a dead replica's claim takes to run out.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    private const int MaxErrorLength = 500;

    private readonly Dictionary<SocialPlatform, ISocialThreadSource> _sources = sources.ToDictionary(s => s.Platform);

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        LogDisabledPlatforms();
        return base.StartAsync(cancellationToken);
    }

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

    /// <summary>Enabled with an empty server or DID disables that platform; said once, at start.</summary>
    private void LogDisabledPlatforms()
    {
        var settings = options.Value;
        if (!settings.Enabled)
            return;

        if (!settings.MastodonConfigured)
            LogPlatformDisabled(logger, SocialPlatform.Mastodon, "Social:Mastodon:Server");
        if (!settings.BlueskyConfigured)
            LogPlatformDisabled(logger, SocialPlatform.Bluesky, "Social:Bluesky:Did");
    }

    /// <summary>One tick: claim what is due, fetch it, store the results. Driven directly by the tests.</summary>
    public async Task<SocialTickResult> TickAsync(BlogDbContext db, CancellationToken ct = default)
    {
        var settings = options.Value;
        List<SocialPlatform> platforms = [];
        if (settings.MastodonConfigured)
            platforms.Add(SocialPlatform.Mastodon);
        if (settings.BlueskyConfigured)
            platforms.Add(SocialPlatform.Bluesky);

        // Off (the default) or nothing configured: no query at all.
        if (!settings.Enabled || platforms.Count == 0)
            return new SocialTickResult(Skipped: true, Claimed: 0);

        var now = Now();
        List<SocialClaim> claims = [];
        var ran = await BackgroundJobLock.TryRunAsync(db, BackgroundJobIds.SocialFetch,
            token => ClaimAsync(db, platforms, now, claims, token), ct);
        if (!ran)
            return new SocialTickResult(Skipped: true, Claimed: 0);

        if (claims.Count == 0)
            return new SocialTickResult(Skipped: false, Claimed: 0);

        var blocks = await LoadBlocksAsync(db, ct);
        Dictionary<SocialPlatform, DateTime> busyUntil = [];
        foreach (var claim in claims)
        {
            db.ChangeTracker.Clear();
            ct.ThrowIfCancellationRequested();

            // A platform that said "not before then" is not asked again this tick: its remaining claims are released.
            if (busyUntil.TryGetValue(claim.Platform, out var until))
            {
                await ReleaseAsync(db, claim, until, ct);
                continue;
            }

            if (await ProcessAsync(db, claim, blocks[claim.Platform], ct) is { } busy)
                busyUntil[claim.Platform] = busy;
        }

        return new SocialTickResult(Skipped: false, Claimed: claims.Count);
    }

    /// <summary>Inside the job lock: the due threads of published posts get a lease (conditional on still being due, so even a broken lock cannot claim one twice).</summary>
    private static async Task ClaimAsync(BlogDbContext db, List<SocialPlatform> platforms, DateTime now, List<SocialClaim> claims, CancellationToken ct)
    {
        var due = await db.SocialThreads
            .AsNoTracking()
            .Where(t => t.NextDueAt <= now && (t.LeaseUntil == null || t.LeaseUntil < now) && platforms.Contains(t.Platform)
                && t.Post.Status == PostStatus.Published && t.Post.PublishedAt <= now)
            .OrderBy(t => t.NextDueAt).ThenBy(t => t.PostId).ThenBy(t => t.Platform)
            .Take(MaxClaimsPerTick)
            .Select(t => new
            {
                t.PostId,
                t.Platform,
                RemoteId = t.Platform == SocialPlatform.Mastodon ? t.Post.MastodonStatusId : t.Post.BlueskyUri,
                t.Post.PublishedAt,
                t.Post.DiscussionLinkedAt,
            })
            .ToListAsync(ct);

        var leaseUntil = now + Lease;
        foreach (var thread in due)
        {
            if (thread.RemoteId is null)
                continue; // the link was cleared and the thread row is about to go

            var claimed = await db.SocialThreads
                .Where(t => t.PostId == thread.PostId && t.Platform == thread.Platform
                    && t.NextDueAt <= now && (t.LeaseUntil == null || t.LeaseUntil < now))
                .ExecuteUpdateAsync(set => set.SetProperty(t => t.LeaseUntil, (DateTime?)leaseUntil), ct);
            if (claimed == 1)
                claims.Add(new SocialClaim(thread.PostId, thread.Platform, thread.RemoteId, Anchor(thread.PublishedAt, thread.DiscussionLinkedAt)));
        }
    }

    private static DateTime Anchor(DateTime? publishedAt, DateTime? linkedAt) =>
        (publishedAt, linkedAt) switch
        {
            ({ } published, { } linked) => published > linked ? published : linked,
            ({ } published, null) => published,
            (null, { } linked) => linked,
            _ => DateTime.MinValue,
        };

    private static async Task<Dictionary<SocialPlatform, SocialBlockSet>> LoadBlocksAsync(BlogDbContext db, CancellationToken ct)
    {
        var all = await db.SocialBlocks.AsNoTracking().Select(b => new { b.Platform, b.Kind, b.ExternalId }).ToListAsync(ct);
        return Enum.GetValues<SocialPlatform>().ToDictionary(
            platform => platform,
            platform => new SocialBlockSet(
                all.Where(b => b.Platform == platform && b.Kind == SocialBlockKind.Reply).Select(b => b.ExternalId),
                all.Where(b => b.Platform == platform && b.Kind == SocialBlockKind.Account).Select(b => b.ExternalId)));
    }

    /// <summary>Fetches one claimed thread and records the outcome; returns the time the platform asked to be left alone until, if it did.</summary>
    private async Task<DateTime?> ProcessAsync(BlogDbContext db, SocialClaim claim, SocialBlockSet blocks, CancellationToken ct)
    {
        try
        {
            SocialFetchOutcome outcome;
            try
            {
                outcome = await gates.RunAsync(claim.Platform, () => _sources[claim.Platform].FetchAsync(claim, blocks, ct), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A fault nobody expected: recorded by its kind only, and the other threads carry on.
                LogFetchFaulted(logger, claim.PostId, claim.Platform, ex.GetType().Name);
                outcome = new SocialFetchOutcome.Failed($"Unexpected error ({ex.GetType().Name})");
            }

            var now = Now();
            switch (outcome)
            {
                case SocialFetchOutcome.Fetched fetched:
                    await StoreAsync(db, claim, fetched.Replies, fetched.Counts, now, ct);
                    return fetched.PlatformBusyUntil;

                case SocialFetchOutcome.RateLimited limited:
                    await ReleaseAsync(db, claim, limited.Until, ct);
                    return limited.Until;

                case SocialFetchOutcome.Failed failed:
                    await RecordFailureAsync(db, claim, failed.Error, now, ct);
                    return null;

                case null:
                    return null;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The result could not be written (the database): the lease runs out and the thread is taken again.
            LogStoreFailed(logger, claim.PostId, claim.Platform, ex.GetType().Name);
            return null;
        }
    }

    /// <summary>The thread's row only when the post still links to the id that was fetched: a link changed meanwhile resets the thread, and this result belongs to the old link.</summary>
    private static IQueryable<SocialThread> StillLinked(BlogDbContext db, SocialClaim claim)
    {
        var threads = db.SocialThreads.Where(t => t.PostId == claim.PostId && t.Platform == claim.Platform);
        return claim.Platform == SocialPlatform.Mastodon
            ? threads.Where(t => db.Posts.Any(p => p.Id == t.PostId && p.MastodonStatusId == claim.RemoteId))
            : threads.Where(t => db.Posts.Any(p => p.Id == t.PostId && p.BlueskyUri == claim.RemoteId));
    }

    private async Task StoreAsync(BlogDbContext db, SocialClaim claim, IReadOnlyList<IngestedReply> replies, SocialCounts? counts, DateTime now, CancellationToken ct)
    {
        var avatarUrls = replies.Select(r => r.AvatarSourceUrl).Where(u => u is not null).Select(u => u!).Distinct(StringComparer.Ordinal).ToList();
        IReadOnlyDictionary<string, string> hashes = avatarUrls.Count == 0
            ? new Dictionary<string, string>()
            : await avatars.ResolveAsync(db, avatarUrls, ct);

        var rows = replies
            .DistinctBy(r => r.ExternalId, StringComparer.Ordinal)
            .Select(r => new SocialReply
            {
                PostId = claim.PostId,
                Platform = claim.Platform,
                ExternalId = r.ExternalId,
                ParentExternalId = r.ParentExternalId,
                AuthorHandle = r.AuthorHandle,
                AuthorDisplayName = r.AuthorDisplayName,
                AuthorUrl = r.AuthorUrl,
                AuthorAccountId = r.AuthorAccountId,
                AvatarHash = r.AvatarSourceUrl is not null && hashes.TryGetValue(r.AvatarSourceUrl, out var hash) ? hash : null,
                CreatedAt = r.CreatedAt.UtcDateTime,
                Permalink = r.Permalink,
                ContentHtml = r.ContentHtml,
                ContentWarning = r.ContentWarning,
                HasMedia = r.HasMedia,
                FetchedAt = now,
            })
            .ToList();
        var snapshot = SocialSnapshot.Hash(rows);
        var nextDue = now + SocialSchedule.Interval(claim.Anchor, now);

        var stored = await db.SocialThreads
            .AsNoTracking()
            .Where(t => t.PostId == claim.PostId && t.Platform == claim.Platform)
            .Select(t => new { t.SnapshotHash, t.LikeCount, t.RepostCount, t.ReplyCount })
            .SingleOrDefaultAsync(ct);

        // The counts are not part of the snapshot hash (it stays reply-only); a count that differs from the stored one (or is the first) is compared here.
        // No counts (the count request failed) changes nothing: the stored ones stay.
        var countsChanged = counts is not null
            && (stored?.LikeCount != counts.Likes || stored.RepostCount != counts.Reposts || stored.ReplyCount != counts.Replies);

        bool changed;
        if (stored?.SnapshotHash == snapshot)
        {
            // Same snapshot: the times move (and the counts, when they changed); the rows stay. Only a changed count invalidates.
            var (linked, notified) = await UpdateTimesAsync(db, claim, counts, countsChanged, now, nextDue, ct);
            if (!linked)
                return;
            changed = notified;
        }
        else
        {
            changed = await ReplaceAsync(db, claim, rows, snapshot, counts, now, nextDue, ct);
            if (!changed)
                return;
        }

        if (changed)
            await notifier.InvalidateLocalAsync(CacheGroup.Discussion);

        await avatars.SweepAsync(db, ct);
    }

    /// <summary>
    /// The same snapshot again: the thread's times move, and its counts when it has new ones. A changed count is written in one transaction
    /// with the NOTIFY (the other replicas drop their discussion entries). <c>Linked</c> is false (nothing written) when the link changed meanwhile.
    /// </summary>
    private async Task<(bool Linked, bool Notified)> UpdateTimesAsync(
        BlogDbContext db, SocialClaim claim, SocialCounts? counts, bool countsChanged, DateTime now, DateTime nextDue, CancellationToken ct)
    {
        await using var transaction = countsChanged ? await db.Database.BeginTransactionAsync(ct) : null;
        var updated = await StillLinked(db, claim).ExecuteUpdateAsync(set =>
        {
            set.SetProperty(t => t.LastSuccessAt, (DateTime?)now)
                .SetProperty(t => t.LastAttemptAt, (DateTime?)now)
                .SetProperty(t => t.NextDueAt, nextDue)
                .SetProperty(t => t.LeaseUntil, (DateTime?)null)
                .SetProperty(t => t.LastError, (string?)null);
            if (counts is not null)
                SetCounts(set, counts, now);
        }, ct);

        if (updated == 0)
        {
            if (transaction is not null)
                await transaction.RollbackAsync(ct);
            return (false, false);
        }

        if (transaction is null)
            return (true, false);

        await notifier.NotifyAsync(db, CacheGroup.Discussion, ct);
        await transaction.CommitAsync(ct);
        return (true, true);
    }

    private static void SetCounts(UpdateSettersBuilder<SocialThread> set, SocialCounts counts, DateTime now) =>
        set.SetProperty(t => t.LikeCount, (int?)counts.Likes)
            .SetProperty(t => t.RepostCount, (int?)counts.Reposts)
            .SetProperty(t => t.ReplyCount, (int?)counts.Replies)
            .SetProperty(t => t.CountsAt, (DateTime?)now);

    /// <summary>One transaction: the thread's state, its replaced rows and the NOTIFY. False (nothing written) when the link changed meanwhile.</summary>
    private async Task<bool> ReplaceAsync(
        BlogDbContext db, SocialClaim claim, List<SocialReply> rows, string snapshot, SocialCounts? counts, DateTime now, DateTime nextDue, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // The thread update comes first and holds the row until the commit: a link change waits for it, and one that got there first makes it affect no row.
        var updated = await StillLinked(db, claim).ExecuteUpdateAsync(set =>
        {
            set.SetProperty(t => t.SnapshotHash, snapshot)
                .SetProperty(t => t.LastSuccessAt, (DateTime?)now)
                .SetProperty(t => t.LastAttemptAt, (DateTime?)now)
                .SetProperty(t => t.NextDueAt, nextDue)
                .SetProperty(t => t.LeaseUntil, (DateTime?)null)
                .SetProperty(t => t.LastError, (string?)null);
            if (counts is not null)
                SetCounts(set, counts, now);
        }, ct);
        if (updated == 0)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        await db.SocialReplies.Where(r => r.PostId == claim.PostId && r.Platform == claim.Platform).ExecuteDeleteAsync(ct);
        db.SocialReplies.AddRange(rows);
        await db.SaveChangesAsync(ct);
        await notifier.NotifyAsync(db, CacheGroup.Discussion, ct);
        await transaction.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return true;
    }

    private static async Task RecordFailureAsync(BlogDbContext db, SocialClaim claim, string error, DateTime now, CancellationToken ct)
    {
        var text = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
        var nextDue = now + SocialSchedule.Interval(claim.Anchor, now);
        await StillLinked(db, claim).ExecuteUpdateAsync(set => set
            .SetProperty(t => t.LastAttemptAt, (DateTime?)now)
            .SetProperty(t => t.LastError, text)
            .SetProperty(t => t.NextDueAt, nextDue)
            .SetProperty(t => t.LeaseUntil, (DateTime?)null), ct);
    }

    private static async Task ReleaseAsync(BlogDbContext db, SocialClaim claim, DateTime until, CancellationToken ct) =>
        await StillLinked(db, claim).ExecuteUpdateAsync(set => set
            .SetProperty(t => t.NextDueAt, until)
            .SetProperty(t => t.LeaseUntil, (DateTime?)null), ct);

    private DateTime Now() => time.GetUtcNow().UtcDateTime;

    [LoggerMessage(Level = LogLevel.Information, Message = "Social replies: {Platform} is disabled because {Setting} is empty.")]
    private static partial void LogPlatformDisabled(ILogger logger, SocialPlatform platform, string setting);

    [LoggerMessage(Level = LogLevel.Error, Message = "The social fetch tick failed ({ExceptionType}).")]
    private static partial void LogTickFailed(ILogger logger, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fetching the {Platform} thread of post {PostId} failed unexpectedly ({ExceptionType}).")]
    private static partial void LogFetchFaulted(ILogger logger, Guid postId, SocialPlatform platform, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Storing the {Platform} thread of post {PostId} failed ({ExceptionType}); its lease will run out.")]
    private static partial void LogStoreFailed(ILogger logger, Guid postId, SocialPlatform platform, string exceptionType);
}
