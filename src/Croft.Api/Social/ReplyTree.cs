using Croft.Api.Models;
using Croft.Shared.Contracts.Social;

namespace Croft.Api.Social;

/// <summary>The nested replies of one thread (or of both, merged) and how many replies there are in all, nested ones included.</summary>
public sealed record ReplyTreeResult(IReadOnlyList<DiscussionReplyDto> Replies, int Count);

/// <summary>
/// Builds what a reader sees from stored rows (brief 12a): answers nested under their parents, oldest first at every level, nested to
/// depth 3. A reply deeper than level 3 is flattened into the level-3 list of its level-2 ancestor, ordered by time with the others.
/// A row whose parent is not stored (cannot happen after a refresh; defensive) is shown at the top level instead of being lost.
/// </summary>
public static class ReplyTree
{
    /// <summary>The deepest level that nests; replies below it are flattened into it.</summary>
    public const int MaxDepth = 3;

    public static ReplyTreeResult Build(IEnumerable<SocialReply> rows)
    {
        var all = rows.ToList();
        var known = all.Select(r => (r.Platform, r.ExternalId)).ToHashSet();
        var children = all.ToLookup(r => (r.Platform, Parent: r.ParentExternalId));

        var top = all
            .Where(r => r.ParentExternalId is null || !known.Contains((r.Platform, r.ParentExternalId)))
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.ExternalId, StringComparer.Ordinal)
            .Select(r => ToDto(r, 1, children))
            .ToList();

        return new ReplyTreeResult(top, all.Count);
    }

    private static DiscussionReplyDto ToDto(SocialReply row, int depth, ILookup<(SocialPlatform Platform, string? Parent), SocialReply> children)
    {
        IEnumerable<SocialReply> below;
        if (depth < MaxDepth - 1)
        {
            below = Direct(row, children);
        }
        else if (depth == MaxDepth - 1)
        {
            // Level 2 holds every deeper reply in one list, oldest first.
            below = Descendants(row, children);
        }
        else
        {
            below = [];
        }

        var replies = below
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.ExternalId, StringComparer.Ordinal)
            .Select(r => ToDto(r, depth + 1, children))
            .ToList();

        return new DiscussionReplyDto(
            row.Platform, row.ExternalId, row.AuthorHandle, row.AuthorDisplayName, row.AuthorUrl, row.AuthorAccountId,
            row.AvatarHash, new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc)), row.Permalink,
            row.ContentHtml, row.ContentWarning, row.HasMedia, replies);
    }

    private static IEnumerable<SocialReply> Direct(SocialReply row, ILookup<(SocialPlatform Platform, string? Parent), SocialReply> children) =>
        children[(row.Platform, row.ExternalId)];

    private static List<SocialReply> Descendants(SocialReply row, ILookup<(SocialPlatform Platform, string? Parent), SocialReply> children)
    {
        var found = new List<SocialReply>();
        var queue = new Queue<SocialReply>(Direct(row, children));
        HashSet<string> seen = [with(StringComparer.Ordinal), row.ExternalId];
        while (queue.Count > 0)
        {
            var next = queue.Dequeue();
            if (!seen.Add(next.ExternalId))
                continue;

            found.Add(next);
            foreach (var child in Direct(next, children))
                queue.Enqueue(child);
        }

        return found;
    }
}
