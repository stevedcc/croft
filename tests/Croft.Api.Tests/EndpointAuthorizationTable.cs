using System.Net.Http.Headers;
using System.Net.Http.Json;
using Croft.Shared.Contracts;
using Croft.Shared.Contracts.Social;

namespace Croft.Api.Tests;

/// <summary>The ids of the rows the denial tests aim their requests at.</summary>
internal sealed record AuthorizationSeed(Guid PostId, Guid FeedbackId, Guid MediaId, Guid CategoryId, Guid TagId, Guid BlockId);

/// <summary>
/// One role-gated API endpoint and what is expected of it: the roles allowed in, and how to send a
/// request to it. <c>Build(seed, real)</c> aims the request at the seeded rows when <c>real</c> is
/// true (a request that must be refused, so the row can be shown untouched afterwards) and at
/// unknown ids when false (a request that must get past authorization, whatever it then answers).
/// </summary>
internal sealed record GatedEndpoint(
    string Method,
    string Pattern,
    string[] Roles,
    Func<AuthorizationSeed, bool, HttpRequestMessage> Build)
{
    public string Key => $"{Method} {Pattern}";

    public override string ToString() => Key;
}

/// <summary>
/// The table every role-gated API endpoint must have a row in. It is the single list the denial
/// tests (<see cref="EndpointAuthorizationTests"/>) run over and the guard
/// (<see cref="EndpointAuthorizationGuardTests"/>) holds the real endpoint list against: a new
/// <c>[Authorize]</c> endpoint with no row here turns the suite red, and a row whose roles differ
/// from the attribute does too. Add the row, with a request that reaches the action, then the
/// denial tests cover it.
/// </summary>
internal static class EndpointAuthorizationTable
{
    private static readonly string[] Admin = ["Admin"];
    private static readonly string[] AdminOrEditor = ["Admin", "Editor"];

    public static IReadOnlyList<GatedEndpoint> Endpoints { get; } =
    [
        // Posts
        new("POST", "api/posts", Admin, (_, _) =>
            Json(HttpMethod.Post, "/api/posts", new CreatePostRequest($"Denial {Guid.NewGuid():N}", "content"))),
        new("PUT", "api/posts/{id:guid}", AdminOrEditor, (s, real) =>
            Json(HttpMethod.Put, $"/api/posts/{Id(real, s.PostId)}", new UpdatePostRequest("Hijacked title", "hijacked"))),
        new("DELETE", "api/posts/{id:guid}", Admin, (s, real) =>
            new HttpRequestMessage(HttpMethod.Delete, $"/api/posts/{Id(real, s.PostId)}")),
        new("POST", "api/posts/{id:guid}/publish", Admin, (s, real) =>
            Json(HttpMethod.Post, $"/api/posts/{Id(real, s.PostId)}/publish", new PublishRequest())),
        new("POST", "api/posts/{id:guid}/unpublish", Admin, (s, real) =>
            new HttpRequestMessage(HttpMethod.Post, $"/api/posts/{Id(real, s.PostId)}/unpublish")),

        // Feedback
        new("GET", "api/posts/{postId:guid}/feedback", AdminOrEditor, (s, real) =>
            new HttpRequestMessage(HttpMethod.Get, $"/api/posts/{Id(real, s.PostId)}/feedback")),
        new("POST", "api/posts/{postId:guid}/feedback", AdminOrEditor, (s, real) =>
            Json(HttpMethod.Post, $"/api/posts/{Id(real, s.PostId)}/feedback", new CreateFeedbackRequest("quoted", "a comment"))),
        new("DELETE", "api/posts/{postId:guid}/feedback/{feedbackId:guid}", AdminOrEditor, (s, real) =>
            new HttpRequestMessage(HttpMethod.Delete, $"/api/posts/{Id(real, s.PostId)}/feedback/{Id(real, s.FeedbackId)}")),
        new("POST", "api/posts/{postId:guid}/feedback/{feedbackId:guid}/resolve", Admin, (s, real) =>
            new HttpRequestMessage(HttpMethod.Post, $"/api/posts/{Id(real, s.PostId)}/feedback/{Id(real, s.FeedbackId)}/resolve")),

        // Media
        new("POST", "api/media", Admin, (_, _) => Upload()),
        new("GET", "api/media", Admin, (_, _) => new HttpRequestMessage(HttpMethod.Get, "/api/media")),
        new("PUT", "api/media/{id:guid}", Admin, (s, real) =>
            Json(HttpMethod.Put, $"/api/media/{Id(real, s.MediaId)}", new UpdateMediaRequest("hijacked alt text"))),
        new("DELETE", "api/media/{id:guid}", Admin, (s, real) =>
            new HttpRequestMessage(HttpMethod.Delete, $"/api/media/{Id(real, s.MediaId)}")),
        new("GET", "api/posts/{postId:guid}/media", AdminOrEditor, (s, real) =>
            new HttpRequestMessage(HttpMethod.Get, $"/api/posts/{Id(real, s.PostId)}/media")),

        // Categories and tags
        new("POST", "api/categories", Admin, (_, _) =>
            Json(HttpMethod.Post, "/api/categories", $"Denial category {Guid.NewGuid():N}")),
        new("PUT", "api/categories/{id:guid}", Admin, (s, real) =>
            Json(HttpMethod.Put, $"/api/categories/{Id(real, s.CategoryId)}", new UpdateCategoryRequest("Hijacked category", null))),
        new("DELETE", "api/categories/{id:guid}", Admin, (s, real) =>
            new HttpRequestMessage(HttpMethod.Delete, $"/api/categories/{Id(real, s.CategoryId)}")),
        new("POST", "api/tags", Admin, (_, _) =>
            Json(HttpMethod.Post, "/api/tags", $"Denial tag {Guid.NewGuid():N}")),
        new("PUT", "api/tags/{id:guid}", Admin, (s, real) =>
            Json(HttpMethod.Put, $"/api/tags/{Id(real, s.TagId)}", new UpdateTagRequest("Hijacked tag"))),
        new("DELETE", "api/tags/{id:guid}", Admin, (s, real) =>
            new HttpRequestMessage(HttpMethod.Delete, $"/api/tags/{Id(real, s.TagId)}")),

        // The discussion below a post (brief 12a)
        new("GET", "api/posts/{id:guid}/discussion/admin", AdminOrEditor, (s, real) =>
            new HttpRequestMessage(HttpMethod.Get, $"/api/posts/{Id(real, s.PostId)}/discussion/admin")),
        new("POST", "api/posts/{id:guid}/discussion/refresh", AdminOrEditor, (s, real) =>
            new HttpRequestMessage(HttpMethod.Post, $"/api/posts/{Id(real, s.PostId)}/discussion/refresh")),
        // Announcing on publish (brief 12d)
        new("GET", "api/posts/{id:guid}/announcements", AdminOrEditor, (s, real) =>
            new HttpRequestMessage(HttpMethod.Get, $"/api/posts/{Id(real, s.PostId)}/announcements")),
        new("POST", "api/posts/{id:guid}/announcements/{platform}", Admin, (s, real) =>
            new HttpRequestMessage(HttpMethod.Post, $"/api/posts/{Id(real, s.PostId)}/announcements/mastodon")),
        new("GET", "api/discussion/blocks", Admin, (_, _) => new HttpRequestMessage(HttpMethod.Get, "/api/discussion/blocks")),
        new("POST", "api/discussion/blocks", AdminOrEditor, (_, _) =>
            Json(HttpMethod.Post, "/api/discussion/blocks", new CreateSocialBlockRequest(
                SocialPlatform.Mastodon, SocialBlockKind.Account, $"denial-{Guid.NewGuid():N}", "Denial", null))),
        new("DELETE", "api/discussion/blocks/{id:guid}", Admin, (s, real) =>
            new HttpRequestMessage(HttpMethod.Delete, $"/api/discussion/blocks/{Id(real, s.BlockId)}")),

        // Analytics
        new("GET", "api/analytics", Admin, (s, real) =>
            new HttpRequestMessage(HttpMethod.Get, $"/api/analytics?postId={Id(real, s.PostId)}")),
    ];

    /// <summary>
    /// Every endpoint that answers an anonymous caller on purpose (<c>[AllowAnonymous]</c>), pinned
    /// so one cannot appear (or a gated one become anonymous) unnoticed. Method and route pattern.
    /// </summary>
    public static IReadOnlyList<string> AnonymousEndpoints { get; } =
    [
        "GET api/categories", "GET api/categories/{slug}", "GET api/media/{id:guid}", "GET api/pages",
        "GET api/pages/navigation", "GET api/pages/navigation/all", "GET api/pages/{slug}",
        "GET api/posts", "GET api/posts/{slug}", "GET api/posts/{slug}/discussion", "GET api/posts/{slug}/related", "GET api/search",
        "GET api/sitemap", "GET api/social/avatars/{hash}.webp", "GET api/tags", "GET api/tags/{slug}", "POST api/analytics/pageview",
    ];

    /// <summary>
    /// Endpoints with no authorization metadata at all, which anyone may call: the health probes
    /// and (in Development only) the OpenAPI document. An action that loses its <c>[Authorize]</c>
    /// lands in this class, so it is pinned too.
    /// </summary>
    public static IReadOnlyList<string> OpenEndpoints { get; } =
    [
        "ANY /health", "ANY /health/live", "ANY /health/ready", "GET /openapi/{documentName}.json",
    ];

    public static GatedEndpoint Find(string key) => Endpoints.Single(e => e.Key == key);

    public static IEnumerable<object[]> Keys => Endpoints.Select(e => new object[] { e.Key });

    private static Guid Id(bool real, Guid seeded) => real ? seeded : Guid.NewGuid();

    private static HttpRequestMessage Json<T>(HttpMethod method, string url, T body) =>
        new(method, url) { Content = JsonContent.Create(body) };

    private static HttpRequestMessage Upload()
    {
        var png = new ByteArrayContent(Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
        png.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return new HttpRequestMessage(HttpMethod.Post, "/api/media")
        {
            Content = new MultipartFormDataContent { { png, "file", "denial.png" } },
        };
    }
}
