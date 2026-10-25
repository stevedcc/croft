using System.Net.Http.Headers;
using Croft.Shared.Contracts;
using Croft.Shared.Contracts.Social;

namespace Croft.Api.Tests.Fixtures;

/// <summary>
/// One API host (TestAuthHandler, a database of its own) with one row of every kind the denial
/// tests aim at, shared by the whole class so the ~20 endpoint cases do not each host the API.
/// A class fixture cannot take another class fixture, so it owns its database fixture.
/// </summary>
public sealed class AuthorizationApiFixture : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _database = new();
    private string? _storagePath;

    internal BlogApiFactory Factory { get; private set; } = null!;

    internal AuthorizationSeed Seed { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _database.InitializeAsync();
        _storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_storagePath);
        Factory = new BlogApiFactory(_database.ConnectionString, _storagePath);

        var admin = Factory.CreateClient().WithUser("seed-admin", "Admin");

        var post = await Created<PostDto>(admin.PostAsJsonAsync(
            "/api/posts", new CreatePostRequest("Seeded for denial tests", "seeded content")));
        var feedback = await Created<FeedbackDto>(
            Factory.CreateClient().WithUser("seed-editor", "Editor").PostAsJsonAsync(
                $"/api/posts/{post.Id}/feedback", new CreateFeedbackRequest("seeded quote", "seeded comment")));
        var category = await Created<CategoryDto>(admin.PostAsJsonAsync("/api/categories", "Seeded category"));
        var tag = await Created<TagDto>(admin.PostAsJsonAsync("/api/tags", "Seeded tag"));

        var png = new ByteArrayContent(Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
        png.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var media = await Created<MediaAssetDto>(admin.PostAsync(
            "/api/media", new MultipartFormDataContent { { png, "file", "seeded.png" } }));

        var block = await Created<SocialBlockDto>(admin.PostAsJsonAsync(
            "/api/discussion/blocks", new CreateSocialBlockRequest(SocialPlatform.Bluesky, SocialBlockKind.Account, "did:plc:seededblock", "Seeded block", null)));

        Seed = new AuthorizationSeed(post.Id, feedback.Id, media.Id, category.Id, tag.Id, block.Id);
    }

    public async ValueTask DisposeAsync()
    {
        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }

        await _database.DisposeAsync();
        if (_storagePath is not null && Directory.Exists(_storagePath))
        {
            Directory.Delete(_storagePath, recursive: true);
        }
    }

    private static async Task<T> Created<T>(Task<HttpResponseMessage> request)
    {
        var response = await request;
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
