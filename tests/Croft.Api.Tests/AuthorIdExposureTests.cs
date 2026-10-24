using System.Net;
using System.Text.Json;
using Croft.Api.Tests.Fixtures;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using AwesomeAssertions;

namespace Croft.Api.Tests;

/// <summary>
/// Brief 13b item 8 (architecture review): <c>PostDto.AuthorId</c> is the author's IdP subject. An
/// anonymous reader of a published post or page must never see it (and neither does the public read
/// cache hold it); staff reads keep it, the editor uses it.
/// </summary>
public class AuthorIdExposureTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    [Fact]
    public async Task AnonymousRead_OfAPublishedPost_HasNoAuthorId_StaffReadHasIt()
    {
        await using var factory = CreateFactory();
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreateAndPublishAsync(admin, "AuthorId Exposure Post", PostKind.Post);

        var anonymous = factory.CreateClient().AsAnonymous();
        // Twice: the second read is the public read cache's.
        for (var read = 1; read <= 2; read++)
        {
            var body = await GetJsonAsync(anonymous, $"/api/posts/{post.Slug}");
            AuthorIdOf(body).Should().BeNull($"anonymous read {read} must not carry the IdP subject");
            body.RootElement.GetProperty("authorName").GetString().Should().Be("Test User", "the display name stays public");
        }

        var staff = await GetJsonAsync(admin, $"/api/posts/{post.Slug}");
        AuthorIdOf(staff).Should().Be("test-user-id", "the editor needs the author id");
    }

    [Fact]
    public async Task AnonymousRead_OfAPublishedPage_HasNoAuthorId_StaffReadHasIt()
    {
        await using var factory = CreateFactory();
        var admin = factory.CreateClient().WithRole("Admin");
        var page = await CreateAndPublishAsync(admin, "AuthorId Exposure Page", PostKind.Page);

        var anonymous = factory.CreateClient().AsAnonymous();
        for (var read = 1; read <= 2; read++)
        {
            AuthorIdOf(await GetJsonAsync(anonymous, $"/api/pages/{page.Slug}")).Should().BeNull($"anonymous read {read}");
        }

        AuthorIdOf(await GetJsonAsync(admin, $"/api/pages/{page.Slug}")).Should().Be("test-user-id");
    }

    [Fact]
    public async Task StaffResponsesToWrites_KeepTheAuthorId()
    {
        await using var factory = CreateFactory();
        var admin = factory.CreateClient().WithRole("Admin");

        var created = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest("AuthorId Exposure Write", "content"));
        var post = (await created.Content.ReadFromJsonAsync<PostDto>())!;

        post.AuthorId.Should().Be("test-user-id");
    }

    private static string? AuthorIdOf(JsonDocument body) =>
        body.RootElement.TryGetProperty("authorId", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<PostDto> CreateAndPublishAsync(HttpClient admin, string title, PostKind kind)
    {
        var create = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest(title, "content", Kind: kind));
        create.EnsureSuccessStatusCode();
        var post = (await create.Content.ReadFromJsonAsync<PostDto>())!;
        (await admin.PostAsJsonAsync($"/api/posts/{post.Id}/publish", new PublishRequest())).EnsureSuccessStatusCode();
        return post;
    }

    private BlogApiFactory CreateFactory()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);
        return new BlogApiFactory(fixture.ConnectionString, storagePath);
    }
}
