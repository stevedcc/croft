using Croft.Api.Data;
using Croft.Api.Errors;
using Croft.Api.Models;
using Croft.Api.Services;
using Croft.Shared.Contracts.Social;
using Croft.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Croft.Api.Social;

public sealed class DiscussionService(
    BlogDbContext db,
    IPublicReadCache cache,
    ICacheNotifier notifier,
    IHttpContextAccessor httpContextAccessor,
    TimeProvider time,
    IOptions<SocialOptions> options) : IDiscussionService
{
    /// <summary>A thread whose last successful fetch is older than this shows no replies: what was deleted meanwhile cannot be known.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(48);

    private const int MaxIdLength = 512;
    private const int MaxLabelLength = 512;
    private const int MaxNoteLength = 2000;

    /// <summary>What the public cache holds for one post: its links and, per thread, the last successful fetch and the nested replies. Not yet judged by the 48-hour rule.</summary>
    private sealed record CachedDiscussion(string? MastodonUrl, string? BlueskyUrl, IReadOnlyList<CachedThread> Threads);

    private sealed record CachedThread(SocialPlatform Platform, DateTime? LastSuccessAt, IReadOnlyList<DiscussionReplyDto> Replies, int Count);

    public async Task<PostDiscussionDto?> GetPublicAsync(string slug, CancellationToken ct = default)
    {
        // Only the anonymous variant is cached (the same boundary the read-replica routing uses); staff always run their queries.
        var cached = PublicReadRequest.IsAnonymousRead(httpContextAccessor.HttpContext)
            ? await cache.GetOrCreateAsync(CacheGroup.Discussion, PostService.CacheKey("discussion", slug), token => LoadAsync(slug, token), ct)
            : await LoadAsync(slug, ct);
        if (cached is null)
            return null;

        // After the cache, so a cached entry cannot outlive the 48-hour rule, and so the setting is not baked into an entry.
        return Compose(cached, time.GetUtcNow().UtcDateTime, options.Value.Enabled);
    }

    private async Task<CachedDiscussion?> LoadAsync(string slug, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var post = await db.Posts.AsNoTracking()
            .Where(p => p.Slug == slug && p.Status == PostStatus.Published && p.PublishedAt <= now)
            .Select(p => new { p.Id, p.MastodonUrl, p.BlueskyUrl })
            .FirstOrDefaultAsync(ct);
        if (post is null)
            return null;

        var (threads, rows) = await ReadThreadsAsync(post.Id, ct);
        return new CachedDiscussion(post.MastodonUrl, post.BlueskyUrl, [.. threads.Select(t =>
        {
            var tree = ReplyTree.Build(rows.Where(r => r.Platform == t.Platform));
            return new CachedThread(t.Platform, t.LastSuccessAt, tree.Replies, tree.Count);
        })]);
    }

    private static PostDiscussionDto Compose(CachedDiscussion cached, DateTime now, bool enabled)
    {
        if (!enabled)
            return new PostDiscussionDto(null, null, 0, []);

        var fresh = cached.Threads.Where(t => t.LastSuccessAt is { } at && now - at <= StaleAfter).ToList();
        var replies = fresh.SelectMany(t => t.Replies)
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.Platform).ThenBy(r => r.ExternalId, StringComparer.Ordinal)
            .ToList();
        return new PostDiscussionDto(cached.MastodonUrl, cached.BlueskyUrl, fresh.Sum(t => t.Count), replies);
    }

    private async Task<(List<SocialThread> Threads, List<SocialReply> Rows)> ReadThreadsAsync(Guid postId, CancellationToken ct)
    {
        var threads = await db.SocialThreads.AsNoTracking().Where(t => t.PostId == postId).OrderBy(t => t.Platform).ToListAsync(ct);
        var rows = threads.Count == 0 ? [] : await db.SocialReplies.AsNoTracking().Where(r => r.PostId == postId).ToListAsync(ct);
        return (threads, rows);
    }

    public async Task<PostDiscussionAdminDto?> GetAdminAsync(Guid postId, CancellationToken ct = default)
    {
        var post = await db.Posts.AsNoTracking().Where(p => p.Id == postId).Select(p => new { p.MastodonUrl, p.BlueskyUrl }).FirstOrDefaultAsync(ct);
        if (post is null)
            return null;

        var now = time.GetUtcNow().UtcDateTime;
        var (threads, rows) = await ReadThreadsAsync(postId, ct);
        var trees = threads.Select(t => ReplyTree.Build(rows.Where(r => r.Platform == t.Platform))).ToList();
        var replies = trees.SelectMany(t => t.Replies)
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.Platform).ThenBy(r => r.ExternalId, StringComparer.Ordinal)
            .ToList();

        return new PostDiscussionAdminDto(
            new PostDiscussionDto(post.MastodonUrl, post.BlueskyUrl, trees.Sum(t => t.Count), replies),
            [.. threads.Select(t => new DiscussionThreadStatusDto(
                t.Platform, Offset(t.LastSuccessAt), Offset(t.LastAttemptAt), t.LastError, Offset(t.NextDueAt),
                rows.Count(r => r.Platform == t.Platform),
                Stale: t.LastSuccessAt is { } at && now - at > StaleAfter))]);
    }

    private static DateTimeOffset? Offset(DateTime? utc) => utc is { } value ? new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)) : null;

    public async Task<bool> RefreshAsync(Guid postId, CancellationToken ct = default)
    {
        if (!await db.Posts.AnyAsync(p => p.Id == postId, ct))
            return false;

        var now = time.GetUtcNow().UtcDateTime;
        await db.SocialThreads.Where(t => t.PostId == postId).ExecuteUpdateAsync(set => set.SetProperty(t => t.NextDueAt, now), ct);
        return true;
    }

    public async Task<IReadOnlyList<SocialBlockDto>> ListBlocksAsync(CancellationToken ct = default)
    {
        var blocks = await db.SocialBlocks.AsNoTracking().OrderByDescending(b => b.CreatedAt).ThenByDescending(b => b.Id).ToListAsync(ct);
        return [.. blocks.Select(ToDto)];
    }

    private static SocialBlockDto ToDto(SocialBlock b) => new(
        b.Id, b.Platform, b.Kind, b.ExternalId, b.Label, b.Note, b.CreatedBy, new DateTimeOffset(DateTime.SpecifyKind(b.CreatedAt, DateTimeKind.Utc)));

    public async Task<SocialBlockResult> CreateBlockAsync(CreateSocialBlockRequest request, string createdBy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var externalId = Validate(request);
        if (await FindAsync(request, externalId, ct) is { } existing)
            return new SocialBlockResult(ToDto(existing), Created: false);

        var block = new SocialBlock
        {
            Id = Guid.CreateVersion7(),
            Platform = request.Platform,
            Kind = request.Kind,
            ExternalId = externalId,
            Label = string.IsNullOrWhiteSpace(request.Label) ? null : request.Label.Trim(),
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
            CreatedBy = createdBy,
            CreatedAt = time.GetUtcNow().UtcDateTime,
        };

        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            db.SocialBlocks.Add(block);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // Another request blocked the same thing a moment ago: its block is the answer.
                await transaction.RollbackAsync(ct);
                db.Entry(block).State = EntityState.Detached;
                var winner = await FindAsync(request, externalId, ct) ?? throw new ConflictException("The block could not be created.");
                return new SocialBlockResult(ToDto(winner), Created: false);
            }

            await RemoveRepliesAsync(block, ct);
            await notifier.NotifyAsync(db, CacheGroup.Discussion, ct);
            await transaction.CommitAsync(ct);
        }

        await notifier.InvalidateLocalAsync(CacheGroup.Discussion);
        return new SocialBlockResult(ToDto(block), Created: true);
    }

    private Task<SocialBlock?> FindAsync(CreateSocialBlockRequest request, string externalId, CancellationToken ct) =>
        db.SocialBlocks.AsNoTracking().FirstOrDefaultAsync(b => b.Platform == request.Platform && b.Kind == request.Kind && b.ExternalId == externalId, ct);

    /// <summary>The trimmed external id; a bad request is a <see cref="ValidationException"/> naming the field.</summary>
    private static string Validate(CreateSocialBlockRequest request)
    {
        if (!Enum.IsDefined(request.Platform))
            throw new ValidationException("Platform is not a known platform.");
        if (!Enum.IsDefined(request.Kind))
            throw new ValidationException("Kind is not a known kind of block.");

        var externalId = request.ExternalId?.Trim();
        if (string.IsNullOrEmpty(externalId))
            throw new ValidationException("ExternalId is required.");
        if (externalId.Length > MaxIdLength)
            throw new ValidationException($"ExternalId must be {MaxIdLength} characters or fewer.");
        if (request.Label?.Trim().Length > MaxLabelLength)
            throw new ValidationException($"Label must be {MaxLabelLength} characters or fewer.");
        if (request.Note?.Trim().Length > MaxNoteLength)
            throw new ValidationException($"Note must be {MaxNoteLength} characters or fewer.");

        return externalId;
    }

    /// <summary>
    /// Deletes the stored replies the block hides and, with each, its whole subtree (the answers below it), on every post; then gives each
    /// affected thread the hash of what is left, so the next fetch of an unchanged thread is recognised as unchanged.
    /// </summary>
    private async Task RemoveRepliesAsync(SocialBlock block, CancellationToken ct)
    {
        var matching = db.SocialReplies.Where(r => r.Platform == block.Platform);
        matching = block.Kind == SocialBlockKind.Reply
            ? matching.Where(r => r.ExternalId == block.ExternalId)
            : matching.Where(r => r.AuthorAccountId == block.ExternalId);

        var seeds = await matching.AsNoTracking().Select(r => new { r.PostId, r.ExternalId }).ToListAsync(ct);
        foreach (var post in seeds.GroupBy(s => s.PostId))
        {
            var all = await db.SocialReplies.AsNoTracking()
                .Where(r => r.PostId == post.Key && r.Platform == block.Platform)
                .Select(r => new { r.ExternalId, r.ParentExternalId })
                .ToListAsync(ct);
            var answers = all.Where(r => r.ParentExternalId is not null).ToLookup(r => r.ParentExternalId!, r => r.ExternalId, StringComparer.Ordinal);

            HashSet<string> doomed = [with(StringComparer.Ordinal), ..post.Select(s => s.ExternalId)];
            var queue = new Queue<string>(doomed);
            while (queue.Count > 0)
            {
                foreach (var answer in answers[queue.Dequeue()])
                {
                    if (doomed.Add(answer))
                        queue.Enqueue(answer);
                }
            }

            var ids = doomed.ToList();
            await db.SocialReplies.Where(r => r.PostId == post.Key && r.Platform == block.Platform && ids.Contains(r.ExternalId)).ExecuteDeleteAsync(ct);

            var remaining = await db.SocialReplies.AsNoTracking().Where(r => r.PostId == post.Key && r.Platform == block.Platform).ToListAsync(ct);
            var hash = SocialSnapshot.Hash(remaining);
            await db.SocialThreads.Where(t => t.PostId == post.Key && t.Platform == block.Platform)
                .ExecuteUpdateAsync(set => set.SetProperty(t => t.SnapshotHash, hash), ct);
        }
    }

    public async Task<bool> DeleteBlockAsync(Guid id, CancellationToken ct = default)
    {
        var block = await db.SocialBlocks.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (block is null)
            return false;

        var now = time.GetUtcNow().UtcDateTime;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            if (await db.SocialBlocks.Where(b => b.Id == id).ExecuteDeleteAsync(ct) == 0)
            {
                await transaction.RollbackAsync(ct);
                return false; // removed by someone else a moment ago
            }

            // The replies come back on the next fetch: every thread of the platform is due now (the hidden ones are not stored, so which would be affected is unknown).
            await db.SocialThreads.Where(t => t.Platform == block.Platform).ExecuteUpdateAsync(set => set.SetProperty(t => t.NextDueAt, now), ct);
            await notifier.NotifyAsync(db, CacheGroup.Discussion, ct);
            await transaction.CommitAsync(ct);
        }

        await notifier.InvalidateLocalAsync(CacheGroup.Discussion);
        return true;
    }
}
