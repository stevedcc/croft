using Croft.Api.Data;
using Croft.Api.Services;
using Croft.Shared.Contracts;
using Croft.Shared.Contracts.Social;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Croft.Api.Social;

/// <summary>One post's announcement counts, per platform; null where that platform has none (yet).</summary>
public sealed record PostSocialCounts(SocialCountsDto? Mastodon, SocialCountsDto? Bluesky)
{
    /// <summary>Both platforms together; the one that has counts when only one does; null when neither has.</summary>
    public SocialCountsDto? Total => (Mastodon, Bluesky) switch
    {
        (null, null) => null,
        ({ } only, null) => only,
        (null, { } only) => only,
        ({ } m, { } b) => new SocialCountsDto(m.Likes + b.Likes, m.Reposts + b.Reposts, m.Replies + b.Replies),
    };
}

/// <summary>The counts of every post that has any, from one cached query (brief 17e).</summary>
public interface ISocialCountsReader
{
    /// <summary>
    /// The counts per post id; empty while social is disabled. Cached under <see cref="CacheGroup.Discussion"/> (the fetcher invalidates it
    /// when a count changes), so attaching the counts to post DTOs after the posts cache never flushes <see cref="CacheGroup.Posts"/>.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, PostSocialCounts>> GetAsync(CancellationToken ct = default);
}

public sealed class SocialCountsReader(BlogDbContext db, IPublicReadCache cache, IOptions<SocialOptions> options) : ISocialCountsReader
{
    public const string CacheKey = "discussion:counts";

    public async Task<IReadOnlyDictionary<Guid, PostSocialCounts>> GetAsync(CancellationToken ct = default) =>
        options.Value.Enabled
            ? await cache.GetOrCreateAsync(CacheGroup.Discussion, CacheKey, LoadAsync, ct)
            : EmptyCounts;

    private static readonly IReadOnlyDictionary<Guid, PostSocialCounts> EmptyCounts = new Dictionary<Guid, PostSocialCounts>();

    private sealed record Row(Guid PostId, SocialPlatform Platform, int? LikeCount, int? RepostCount, int? ReplyCount);

    private async Task<IReadOnlyDictionary<Guid, PostSocialCounts>> LoadAsync(CancellationToken ct)
    {
        var rows = await db.SocialThreads.AsNoTracking()
            .Where(t => t.LikeCount != null)
            .Select(t => new Row(t.PostId, t.Platform, t.LikeCount, t.RepostCount, t.ReplyCount))
            .ToListAsync(ct);

        return rows.GroupBy(r => r.PostId).ToDictionary(
            g => g.Key,
            g => new PostSocialCounts(Pick(g, SocialPlatform.Mastodon), Pick(g, SocialPlatform.Bluesky)));
    }

    private static SocialCountsDto? Pick(IEnumerable<Row> rows, SocialPlatform platform) =>
        rows.Where(r => r.Platform == platform).Select(r => new SocialCountsDto(r.LikeCount ?? 0, r.RepostCount ?? 0, r.ReplyCount ?? 0)).SingleOrDefault();
}

/// <summary>Attaches the counts to the DTOs a public read returns, after the cache the DTOs came from.</summary>
public static class SocialCountsAttach
{
    public static async Task<PostDto> WithSocialAsync(this ISocialCountsReader reader, PostDto post, CancellationToken ct = default) =>
        (await reader.GetAsync(ct)).TryGetValue(post.Id, out var counts) ? post with { Social = counts.Total } : post;

    public static async Task<IReadOnlyList<PostSummaryDto>> WithSocialAsync(
        this ISocialCountsReader reader, IReadOnlyList<PostSummaryDto> posts, CancellationToken ct = default)
    {
        if (posts.Count == 0)
            return posts;

        var counts = await reader.GetAsync(ct);
        return counts.Count == 0
            ? posts
            : [.. posts.Select(p => counts.TryGetValue(p.Id, out var c) ? p with { Social = c.Total } : p)];
    }
}
