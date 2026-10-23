using System.Globalization;
using System.Text.Json;

namespace Croft.Api.Social;

/// <summary>
/// Reads a Bluesky post thread (<c>app.bsky.feed.getPostThread</c>) into the replies Croft stores (brief 12a). The platform does not
/// filter for us, so this does, before anything is stored: no deleted (<c>notFoundPost</c>) or blocked (<c>blockedPost</c>) post, no
/// author who carries the label <c>!no-unauthenticated</c>, no reply the owner hid in the thread gate, nothing labelled
/// <c>porn</c>, <c>sexual</c>, <c>nudity</c> or <c>graphic-media</c>, nothing on the owner's blocklist. A dropped reply takes its answers with it.
/// </summary>
public static class BlueskyThreadParser
{
    private const string ThreadViewPost = "app.bsky.feed.defs#threadViewPost";
    private const string NoUnauthenticated = "!no-unauthenticated";
    private static readonly string[] AdultLabels = ["porn", "sexual", "nudity", "graphic-media"];
    private static readonly string[] MediaEmbeds = ["app.bsky.embed.images", "app.bsky.embed.video", "app.bsky.embed.recordWithMedia"];

    public static IReadOnlyList<IngestedReply> Parse(string threadJson, SocialBlockSet blocks)
    {
        using var document = SocialJson.Parse(threadJson, "Bluesky thread");
        var thread = document.RootElement.Prop("thread");
        if (!thread.IsObject())
            throw new SocialParseException("The Bluesky thread answer has no thread.");

        // A post that was deleted or blocked has no replies to show; that is an answer, not a failure.
        if (thread.Str("$type") != ThreadViewPost)
            return [];

        var root = thread.Prop("post");
        if (root.Str("uri") is null)
            throw new SocialParseException("The Bluesky thread's post has no uri.");

        var hidden = HiddenReplies(root.Prop("threadgate")).Concat(HiddenReplies(document.RootElement.Prop("threadgate")))
            .ToHashSet(StringComparer.Ordinal);

        var replies = new List<IngestedReply>();
        Walk(thread.Prop("replies"), parent: null, hidden, blocks, replies);
        return replies;
    }

    private static IEnumerable<string> HiddenReplies(JsonElement threadgate) =>
        threadgate.Prop("record").Prop("hiddenReplies").Items()
            .Where(h => h.ValueKind == JsonValueKind.String).Select(h => h.GetString()!);

    private static void Walk(JsonElement nodes, string? parent, HashSet<string> hidden, SocialBlockSet blocks, List<IngestedReply> output)
    {
        foreach (var node in nodes.Items())
        {
            // notFoundPost, blockedPost and anything unknown: dropped, and their subtree is never visited.
            if (node.Str("$type") != ThreadViewPost || TryRead(node.Prop("post"), parent, hidden, blocks) is not { } reply)
                continue;

            output.Add(reply);
            Walk(node.Prop("replies"), reply.ExternalId, hidden, blocks, output);
        }
    }

    private static IngestedReply? TryRead(JsonElement post, string? parent, HashSet<string> hidden, SocialBlockSet blocks)
    {
        var uri = post.Str("uri");
        var author = post.Prop("author");
        var did = author.Str("did");
        if (string.IsNullOrEmpty(uri) || uri.Length > MastodonThreadParser.MaxIdLength || string.IsNullOrEmpty(did) || !author.IsObject())
            return null;

        if (hidden.Contains(uri) || blocks.IsBlocked(uri, did) || IsFilteredByLabels(post, author))
            return null;

        var record = post.Prop("record");
        if (!TryCreatedAt(record.Str("createdAt"), post.Str("indexedAt"), out var createdAt))
            return null;

        var handle = author.Str("handle") is { Length: > 0 } h ? h : did;
        var profile = handle == "handle.invalid" ? did : handle;
        var recordKey = uri[(uri.LastIndexOf('/') + 1)..];

        return new IngestedReply(
            ExternalId: uri,
            ParentExternalId: parent,
            AuthorHandle: handle,
            AuthorDisplayName: author.Str("displayName") is { } name && !string.IsNullOrWhiteSpace(name) ? name.Trim() : handle,
            AuthorUrl: $"https://bsky.app/profile/{profile}",
            AuthorAccountId: did,
            AvatarSourceUrl: SocialUrl.SafeHttps(author.Str("avatar")),
            CreatedAt: createdAt,
            Permalink: $"https://bsky.app/profile/{profile}/post/{recordKey}",
            ContentHtml: SocialHtml.FromBluesky(record.Str("text") ?? string.Empty, record.Prop("facets")),
            ContentWarning: null,
            HasMedia: HasMedia(post.Prop("embed")));
    }

    /// <summary>
    /// The post's own labels, its author's, and the author's self-labels on the record: any of them asking not to be shown to logged-out
    /// users, or marking adult or graphic content, hides the reply. A negated label (<c>neg</c>) cancels itself.
    /// </summary>
    private static bool IsFilteredByLabels(JsonElement post, JsonElement author)
    {
        var labels = post.Prop("labels").Items().Concat(author.Prop("labels").Items())
            .Concat(post.Prop("record").Prop("labels").Prop("values").Items())
            .Where(label => !label.IsTrue("neg"))
            .Select(label => label.Str("val"));

        return labels.Any(val => val == NoUnauthenticated || (val is not null && AdultLabels.Contains(val)));
    }

    private static bool HasMedia(JsonElement embed) =>
        embed.Str("$type") is { } type && MediaEmbeds.Any(prefix => type.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>The author's claimed time and the time the server saw the post: the earlier of the two, so no reply can claim the future.</summary>
    private static bool TryCreatedAt(string? claimed, string? indexed, out DateTimeOffset createdAt)
    {
        var ok = false;
        createdAt = DateTimeOffset.MaxValue;
        foreach (var text in new[] { claimed, indexed })
        {
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                ok = true;
                if (parsed < createdAt)
                    createdAt = parsed;
            }
        }

        return ok;
    }
}
