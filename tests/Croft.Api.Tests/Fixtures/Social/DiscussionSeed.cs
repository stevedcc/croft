using System.Net.Http.Json;
using Croft.Api.Data;
using Croft.Api.Models;
using Croft.Shared.Contracts;
using Croft.Shared.Contracts.Social;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Api.Tests.Fixtures.Social;

/// <summary>One stored reply to seed: its id, its parent (null: an answer to the post), its time and its author's account.</summary>
internal sealed record ReplySpec(string Id, string? Parent, DateTime At, string Account = "account-1");

/// <summary>Posts with discussion links and the replies a refresh would have stored, put in place directly (no platform is called).</summary>
internal static class DiscussionSeed
{
    public const string MastodonLink = "https://mastodon.example/@owner/110054900866478823";
    public static readonly string BlueskyLink = $"https://bsky.app/profile/{SocialTestSettings.Did}/post/3l6oveex3ii2l";

    public static readonly DateTime Noon = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    public static async Task<PostDto> PostAsync(HttpClient admin, string title, bool publish = true, bool mastodon = true, bool bluesky = true, DateTime? publishAt = null)
    {
        var created = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest(
            title, "body", MastodonUrl: mastodon ? MastodonLink : null, BlueskyUrl: bluesky ? BlueskyLink : null));
        created.EnsureSuccessStatusCode();
        var post = (await created.Content.ReadFromJsonAsync<PostDto>())!;
        if (publish)
        {
            var published = await admin.PostAsJsonAsync($"/api/posts/{post.Id}/publish", new PublishRequest(publishAt));
            published.EnsureSuccessStatusCode();
            post = (await published.Content.ReadFromJsonAsync<PostDto>())!;
        }

        return post;
    }

    /// <summary>Replaces the platform's stored replies and gives the thread a successful fetch at <paramref name="lastSuccessAt"/>.</summary>
    public static async Task StoreAsync(BlogApiFactory factory, Guid postId, SocialPlatform platform, DateTime lastSuccessAt, params ReplySpec[] replies)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        var thread = await db.SocialThreads.SingleAsync(t => t.PostId == postId && t.Platform == platform);
        db.SocialReplies.RemoveRange(await db.SocialReplies.Where(r => r.PostId == postId && r.Platform == platform).ToListAsync());
        var rows = replies.Select(r => new SocialReply
        {
            PostId = postId,
            Platform = platform,
            ExternalId = r.Id,
            ParentExternalId = r.Parent,
            AuthorHandle = $"handle-{r.Account}",
            AuthorDisplayName = $"Name {r.Account}",
            AuthorUrl = $"https://social.example/@{r.Account}",
            AuthorAccountId = r.Account,
            CreatedAt = r.At,
            Permalink = $"https://social.example/@{r.Account}/{r.Id}",
            ContentHtml = $"<p>Example reply text {r.Id}.</p>",
            FetchedAt = lastSuccessAt,
        }).ToList();
        db.SocialReplies.AddRange(rows);
        thread.LastSuccessAt = lastSuccessAt;
        thread.LastAttemptAt = lastSuccessAt;
        thread.SnapshotHash = Croft.Api.Social.SocialSnapshot.Hash(rows);
        thread.NextDueAt = lastSuccessAt.AddMinutes(10);
        await db.SaveChangesAsync();
    }

    public static async Task<List<string>> StoredIdsAsync(BlogApiFactory factory, Guid postId, SocialPlatform platform)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        return await db.SocialReplies.AsNoTracking().Where(r => r.PostId == postId && r.Platform == platform).OrderBy(r => r.ExternalId).Select(r => r.ExternalId).ToListAsync();
    }

    public static async Task<PostDiscussionDto> ReadAsync(HttpClient client, string slug)
    {
        var response = await client.GetAsync($"/api/posts/{slug}/discussion");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PostDiscussionDto>())!;
    }

    public static string[] Shape(IEnumerable<DiscussionReplyDto> replies) =>
        [.. replies.Select(r => r.Replies.Count == 0 ? r.ExternalId : $"{r.ExternalId}[{string.Join(",", Shape(r.Replies))}]")];
}
