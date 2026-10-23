using System.Text.Json;
using System.Text.Json.Nodes;

namespace Croft.Api.Tests.Fixtures.Social;

/// <summary>
/// The anonymised platform answers the social tests start from (<c>Fixtures/Social/*.json</c>, copies of
/// <c>docs/briefs/12a-fixtures</c>: every name, handle, text, avatar and URL is invented). A test takes the shape of a real
/// answer and edits in the one case it is about; nothing here calls a real platform.
/// </summary>
internal static class SocialFixtures
{
    public static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Social", file));

    public static JsonNode Load(string file) => JsonNode.Parse(Read(file))!;

    /// <summary>The recorded Mastodon context (<c>GET /api/v1/statuses/{id}/context</c>): the root's id and four descendants.</summary>
    public const string MastodonRootId = "110054900866478823";

    public static JsonNode MastodonContext() => Load("mastodon-context.json");

    public static JsonNode BlueskyThread() => Load("bluesky-thread.json");
}

/// <summary>Builds a Mastodon context answer from the recorded status shape; every status is a clone of a real one with its ids replaced.</summary>
internal sealed class MastodonThreadBuilder
{
    public const string RootId = SocialFixtures.MastodonRootId;

    private readonly List<JsonObject> _statuses = [];

    /// <summary>
    /// Adds a descendant: <paramref name="id"/> answering <paramref name="parentId"/> (the root's id for a top-level reply).
    /// Each status gets an account of its own (<c>acct-{id}</c>) unless <paramref name="edit"/> says otherwise.
    /// </summary>
    public MastodonThreadBuilder Reply(string id, string parentId, Action<JsonObject>? edit = null)
    {
        var status = (JsonObject)SocialFixtures.MastodonContext()["descendants"]![0]!.DeepClone();
        status["id"] = id;
        status["in_reply_to_id"] = parentId;
        status["content"] = $"<p>Example reply text {id}.</p>";
        status["url"] = $"https://social.example/url/{id}";
        status["uri"] = $"https://social.example/uri/{id}";
        var account = (JsonObject)status["account"]!;
        account["id"] = $"acct-{id}";
        account["username"] = $"reader{id}";
        account["acct"] = $"reader{id}";
        account["display_name"] = $"Reader {id}";
        account["url"] = $"https://social.example/@reader{id}";
        account["avatar_static"] = $"https://social.example/avatar/{id}";
        account.Remove("noindex");
        edit?.Invoke(status);
        _statuses.Add(status);
        return this;
    }

    public string ToJson() => new JsonObject
    {
        ["ancestors"] = new JsonArray(),
        ["descendants"] = new JsonArray([.. _statuses.Select(s => (JsonNode)s.DeepClone())]),
    }.ToJsonString();
}

/// <summary>Builds a Bluesky <c>getPostThread</c> answer from the recorded shape.</summary>
internal static class BlueskyThreadBuilder
{
    public const string OwnerDid = "did:plc:ownerowner00000000000000";
    public const string RootUri = $"at://{OwnerDid}/app.bsky.feed.post/3lroot000000a";

    /// <summary>A reply node (<c>threadViewPost</c>) with its answers: a clone of a recorded reply, with the author and ids replaced.</summary>
    public static JsonObject Node(string rkey, Action<JsonObject>? editPost = null, params JsonObject[] replies)
    {
        var node = (JsonObject)SocialFixtures.BlueskyThread()["thread"]!["replies"]![0]!.DeepClone();
        var post = (JsonObject)node["post"]!;
        var did = $"did:plc:{rkey}";
        post["uri"] = $"at://{did}/app.bsky.feed.post/{rkey}";
        post["cid"] = $"cid{rkey}";
        var author = (JsonObject)post["author"]!;
        author["did"] = did;
        author["handle"] = $"{rkey}.example";
        author["displayName"] = $"Reader {rkey}";
        author["avatar"] = $"https://social.example/avatar/{rkey}";
        author["labels"] = new JsonArray();
        post["labels"] = new JsonArray();
        var record = (JsonObject)post["record"]!;
        record["text"] = $"Example text {rkey}.";
        record.Remove("facets");
        record["createdAt"] = "2026-10-20T10:00:00.000Z";
        post["indexedAt"] = "2026-10-20T10:00:00.500Z";
        post.Remove("embed");
        editPost?.Invoke(post);
        node["replies"] = new JsonArray([.. replies]);
        return node;
    }

    public static string UriOf(JsonObject node) => (string)node["post"]!["uri"]!;

    public static JsonObject NotFound(string uri) => new() { ["$type"] = "app.bsky.feed.defs#notFoundPost", ["uri"] = uri, ["notFound"] = true };

    public static JsonObject Blocked(string uri) => new()
    {
        ["$type"] = "app.bsky.feed.defs#blockedPost",
        ["uri"] = uri,
        ["blocked"] = true,
        ["author"] = new JsonObject(),
    };

    public static JsonArray Labels(params string[] values) =>
        new([.. values.Select(v => (JsonNode)new JsonObject { ["val"] = v, ["src"] = "did:plc:labeler" })]);

    /// <summary>The whole answer: the owner's root post with these top-level replies, optionally with the owner's hidden replies in the thread gate.</summary>
    public static string Thread(JsonObject[] replies, params string[] hiddenReplies)
    {
        var thread = (JsonObject)SocialFixtures.BlueskyThread()["thread"]!.DeepClone();
        var post = (JsonObject)thread["post"]!;
        post["uri"] = RootUri;
        ((JsonObject)post["author"]!)["did"] = OwnerDid;
        thread["replies"] = new JsonArray([.. replies]);
        post["threadgate"] = new JsonObject
        {
            ["uri"] = $"at://{OwnerDid}/app.bsky.feed.threadgate/3lroot000000a",
            ["record"] = new JsonObject
            {
                ["$type"] = "app.bsky.feed.threadgate",
                ["hiddenReplies"] = new JsonArray([.. hiddenReplies.Select(h => (JsonNode)h)]),
            },
        };
        return new JsonObject { ["thread"] = thread }.ToJsonString();
    }

    public static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
