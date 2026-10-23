using System.Security.Cryptography;
using Croft.Shared.Contracts;
using Npgsql;

namespace Croft.Web.E2E;

/// <summary>
/// Seeds the discussion under a post for the brief 12b tests, straight into brief 12a's tables
/// (SocialThreads, SocialReplies, SocialAvatars): the E2E instances run with Social:Enabled on and no
/// Mastodon server and no Bluesky DID, so the API's fetcher never runs and shows what is stored. No test calls a real platform.
/// Names and texts are invented.
/// </summary>
internal static class DiscussionSeed
{
    /// <summary>A valid 1 x 1 WebP, the stored form of an avatar (the API serves it as it is).</summary>
    private static readonly byte[] Webp = Convert.FromBase64String("UklGRhoAAABXRUJQVlA4TA0AAAAvAAAAEAcQERGIiP4HAA==");

    private const int Mastodon = 1;
    private const int Bluesky = 2;

    /// <summary>The seeded post: its slug, and the two announcements it links.</summary>
    internal sealed record SeededPost(Guid Id, string Slug, string MastodonUrl, string BlueskyUrl, string AvatarHash);

    /// <summary>One reply to store. <c>Parent</c> is the external id of the reply it answers; <c>Account</c> is the author's account id (default: one account per reply).</summary>
    internal sealed record Reply(
        int Platform, string Id, string? Parent, string Name, string Handle, TimeSpan Age, string Html,
        string? ContentWarning = null, bool HasMedia = false, bool WithAvatar = false, string? Account = null);

    /// <summary>Creates and publishes a post through the API, as the mock admin; returns its id and slug.</summary>
    public static async Task<(Guid Id, string Slug)> PublishPostAsync(string apiUrl, string title, string body)
    {
        using var client = MockIdpApi.CreateClient();
        var token = await MockIdpApi.GetAdminAccessTokenAsync(client);
        var created = await MockIdpApi.PostJsonAsync(client, apiUrl, "/api/posts", new CreatePostRequest(title, body), token);
        var id = created.RootElement.GetProperty("id").GetString()!;
        var published = await MockIdpApi.PostJsonAsync(client, apiUrl, $"/api/posts/{id}/publish", "{}", token);
        return (Guid.Parse(id), published.RootElement.GetProperty("slug").GetString()!);
    }

    /// <summary>Links the post to its two announcements (as 12a stores them) and marks both threads as fetched just now (the 48-hour rule).</summary>
    public static async Task<SeededPost> LinkAsync(NpgsqlConnection db, Guid postId, string slug, string runId, bool withThreads = true)
    {
        var mastodonUrl = $"https://social.example/@owner/{runId}";
        var blueskyUrl = $"https://bsky.example/profile/owner.example/post/{runId}";
        var blueskyUri = $"at://did:plc:ownerexample/app.bsky.feed.post/{runId}";

        await Execute(db, """
            UPDATE "Posts" SET "MastodonUrl" = @murl, "MastodonStatusId" = @mid, "BlueskyUrl" = @burl, "BlueskyUri" = @buri
            WHERE "Id" = @id
            """, ("murl", mastodonUrl), ("mid", runId),
            ("burl", blueskyUrl), ("buri", blueskyUri), ("id", postId));

        if (withThreads)
        {
            foreach (var platform in new[] { Mastodon, Bluesky })
            {
                await Execute(db, """
                    INSERT INTO "SocialThreads" ("PostId", "Platform", "LastSuccessAt", "LastAttemptAt", "NextDueAt")
                    VALUES (@id, @platform, now(), now(), now() + interval '30 days')
                    """, ("id", postId), ("platform", platform));
            }
        }

        return new SeededPost(postId, slug, mastodonUrl, blueskyUrl, AvatarHash);
    }

    /// <summary>The hash the stored avatar is served under: the SHA-256 of its bytes, 64 lower-case hex characters.</summary>
    public static string AvatarHash { get; } = Convert.ToHexStringLower(SHA256.HashData(Webp));

    /// <summary>Stores the replies, and (once per instance) the avatar file and its row, in the instance's uploads folder.</summary>
    public static async Task AddRepliesAsync(NpgsqlConnection db, int instance, SeededPost post, IEnumerable<Reply> replies)
    {
        var folder = Path.Combine($"/tmp/blog-e2e-{instance}-uploads", "social-avatars");
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, $"{AvatarHash}.webp"), Webp);
        await Execute(db, """
            INSERT INTO "SocialAvatars" ("Hash", "SourceUrl", "StoragePath", "FetchedAt")
            VALUES (@hash, 'https://social.example/avatars/bram.png', @path, now())
            ON CONFLICT ("Hash") DO NOTHING
            """, ("hash", AvatarHash), ("path", $"social-avatars/{AvatarHash}.webp"));

        foreach (var reply in replies)
        {
            var slug = reply.Id;
            var host = reply.Platform == Mastodon ? "https://social.example" : "https://bsky.example";
            await Execute(db, """
                INSERT INTO "SocialReplies" ("PostId", "Platform", "ExternalId", "ParentExternalId", "AuthorHandle", "AuthorDisplayName",
                    "AuthorUrl", "AuthorAccountId", "AvatarHash", "CreatedAt", "Permalink", "ContentHtml", "ContentWarning", "HasMedia", "FetchedAt")
                VALUES (@post, @platform, @id, @parent, @handle, @name, @authorUrl, @account, @avatar, now() - @age, @permalink, @html, @cw, @media, now())
                """,
                ("post", post.Id), ("platform", reply.Platform), ("id", reply.Id), ("parent", (object?)reply.Parent ?? DBNull.Value),
                ("handle", reply.Handle), ("name", reply.Name), ("authorUrl", $"{host}/@{slug}"), ("account", reply.Account ?? $"acct-{slug}"),
                ("avatar", reply.WithAvatar ? AvatarHash : (object)DBNull.Value), ("age", reply.Age), ("permalink", Permalink(reply)),
                ("html", reply.Html), ("cw", (object?)reply.ContentWarning ?? DBNull.Value), ("media", reply.HasMedia));
        }
    }

    public static string Permalink(Reply reply) =>
        reply.Platform == Mastodon ? $"https://social.example/@owner/{reply.Id}" : $"https://bsky.example/profile/{reply.Handle}/post/{reply.Id}";

    /// <summary>
    /// The thread the tests read: four top-level replies from both platforms, oldest first (one behind a content warning, the
    /// Bluesky one, one without an avatar that has an answer, one with media), and the answer, which has a stored avatar. Five replies in all.
    /// </summary>
    public static IReadOnlyList<Reply> TheThread(string runId) =>
    [
        new(Mastodon, $"cw{runId}", null, "Cleo Demo", "cleo@social.example", TimeSpan.FromHours(5),
            "<p>The hidden twist is that the gardener did it.</p>", ContentWarning: "Spoilers about the ending"),
        new(Bluesky, $"bs{runId}", null, "Eli Placeholder", "eli.example", TimeSpan.FromHours(4),
            "<p>Bluesky says hello from the other network.</p>"),
        new(Mastodon, $"ada{runId}", null, "Ada Example", "ada@social.example", TimeSpan.FromHours(3),
            "<p>The garden path looks wonderful.</p>"),
        new(Mastodon, $"bram{runId}", $"ada{runId}", "Bram Sample", "bram@social.example", TimeSpan.FromHours(2),
            "<p>Agreed, <a href=\"https://example.org/paths\" rel=\"nofollow ugc noopener\">this guide</a> helps.</p>", WithAvatar: true),
        new(Mastodon, $"dov{runId}", null, "Dov Test", "dov@social.example", TimeSpan.FromHours(1),
            "<p>A photo of my own path.</p>", HasMedia: true),
    ];

    private static async Task Execute(NpgsqlConnection db, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = db.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }
}
