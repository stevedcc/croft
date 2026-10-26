using System.Globalization;
using System.Text.Json;

namespace Croft.Api.Social;

/// <summary>
/// Reads a Mastodon status context (<c>GET /api/v1/statuses/{id}/context</c>, <c>descendants</c> only) into the replies Croft stores
/// (brief 12a). Filtered here, before anything is stored: only <c>public</c> and <c>unlisted</c> statuses, no account that set
/// <c>noindex</c>, nothing on the owner's blocklist. <c>indexable: false</c> is a search opt-in and does not hide a reply. A reply whose
/// ancestors do not all survive, up to the post itself, is dropped with them.
/// </summary>
public static class MastodonThreadParser
{
    /// <summary>The longest id the replies table stores.</summary>
    public const int MaxIdLength = 512;

    /// <summary>
    /// The status's own counts (<c>GET /api/v1/statuses/{id}</c>: <c>favourites_count</c>, <c>reblogs_count</c>, <c>replies_count</c>); null when the
    /// answer is not JSON or lacks any of them. Never throws: the counts are an extra, not part of the thread.
    /// </summary>
    public static SocialCounts? ParseCounts(string statusJson)
    {
        try
        {
            using var document = SocialJson.Parse(statusJson, "Mastodon status");
            var status = document.RootElement;
            return status.Count("favourites_count") is { } likes && status.Count("reblogs_count") is { } reposts && status.Count("replies_count") is { } replies
                ? new SocialCounts(likes, reposts, replies)
                : null;
        }
        catch (SocialParseException)
        {
            return null;
        }
    }

    public static IReadOnlyList<IngestedReply> Parse(string contextJson, string rootStatusId, string server, SocialBlockSet blocks)
    {
        using var document = SocialJson.Parse(contextJson, "Mastodon context");
        var descendants = document.RootElement.Prop("descendants");
        if (descendants.ValueKind != JsonValueKind.Array)
            throw new SocialParseException("The Mastodon context answer has no descendants.");

        var candidates = new List<IngestedReply>();
        HashSet<string> seen = [with(StringComparer.Ordinal)];
        foreach (var status in descendants.EnumerateArray())
        {
            if (TryRead(status, rootStatusId, server, blocks) is { } reply && seen.Add(reply.ExternalId))
                candidates.Add(reply);
        }

        return ReachableFromRoot(candidates);
    }

    private static IngestedReply? TryRead(JsonElement status, string rootStatusId, string server, SocialBlockSet blocks)
    {
        var id = status.Str("id");
        if (string.IsNullOrEmpty(id) || id.Length > MaxIdLength)
            return null;

        if (status.Str("visibility") is not ("public" or "unlisted"))
            return null;

        var account = status.Prop("account");
        var accountId = account.Str("id");
        if (!account.IsObject() || string.IsNullOrEmpty(accountId) || account.IsTrue("noindex") || blocks.IsBlocked(id, accountId))
            return null;

        if (!DateTimeOffset.TryParse(status.Str("created_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var createdAt))
            return null;

        var parent = status.Str("in_reply_to_id");
        var acct = account.Str("acct") is { Length: > 0 } a ? a : account.Str("username") ?? string.Empty;
        var username = account.Str("username");
        var displayName = account.Str("display_name") is { } d && !string.IsNullOrWhiteSpace(d) ? d.Trim() : username ?? acct;

        return new IngestedReply(
            ExternalId: id,
            ParentExternalId: parent == rootStatusId ? null : parent,
            AuthorHandle: acct.Contains('@', StringComparison.Ordinal) ? acct : $"{acct}@{server}",
            AuthorDisplayName: displayName,
            AuthorUrl: SocialUrl.SafeHttp(account.Str("url")) ?? $"https://{server}/@{acct}",
            AuthorAccountId: accountId,
            AvatarSourceUrl: SocialUrl.SafeHttps(account.Str("avatar_static")),
            CreatedAt: createdAt,
            Permalink: SocialUrl.SafeHttp(status.Str("url")) ?? $"https://{server}/@{acct}/{id}",
            ContentHtml: SocialHtml.FromMastodon(status.Str("content") ?? string.Empty),
            ContentWarning: ContentWarning(status),
            HasMedia: status.Prop("media_attachments").Items().Any());
    }

    private static string? ContentWarning(JsonElement status)
    {
        if (status.Str("spoiler_text") is { } spoiler && !string.IsNullOrWhiteSpace(spoiler))
            return spoiler.Trim();

        return status.IsTrue("sensitive") ? "Sensitive content" : null;
    }

    /// <summary>
    /// Keeps the replies that can be reached from the post through kept replies only (a breadth-first walk from the post): a dropped
    /// reply's answers, an answer to a reply that is not in the thread, and a cycle all fall away. Document order is kept.
    /// </summary>
    private static List<IngestedReply> ReachableFromRoot(List<IngestedReply> candidates)
    {
        var children = candidates.ToLookup(r => r.ParentExternalId, StringComparer.Ordinal);
        HashSet<string> reachable = [with(StringComparer.Ordinal)];
        var queue = new Queue<string?>();
        queue.Enqueue(null);
        while (queue.Count > 0)
        {
            foreach (var child in children[queue.Dequeue()])
            {
                if (reachable.Add(child.ExternalId))
                    queue.Enqueue(child.ExternalId);
            }
        }

        return [.. candidates.Where(r => reachable.Contains(r.ExternalId))];
    }
}
