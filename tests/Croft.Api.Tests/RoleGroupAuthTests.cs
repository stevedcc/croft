using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Croft.Api.Tests.Fixtures;
using Croft.Shared.Contracts;
using AwesomeAssertions;

namespace Croft.Api.Tests;

/// <summary>
/// The role-group mapping on the API's real token-validation path: the JWT carries the group
/// claims and the Oidc:RoleGroups settings decide which group grants Admin.
/// </summary>
public class RoleGroupAuthTests(PostgresDatabaseFixture fixture)
    : IClassFixture<PostgresDatabaseFixture>
{
    [Fact]
    public async Task AdminEndpoint_TokenWithConfiguredAdminGroup_IsAuthorisedAsAdmin()
    {
        await using var factory = CreateFactoryAsync();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Create(
            new Claim("sub", "user-1"),
            new Claim("name", "Test User"),
            new Claim("groups", "Blog Admins")));

        var response = await client.PostAsJsonAsync("/api/posts", new CreatePostRequest("Role group post", "content"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task AdminEndpoint_TokenWithUnmappedAdminGroup_IsForbidden()
    {
        await using var factory = CreateFactoryAsync();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Create(
            new Claim("sub", "user-1"),
            new Claim("name", "Test User"),
            new Claim("groups", "Admin")));

        var response = await client.PostAsJsonAsync("/api/posts", new CreatePostRequest("Unmapped group post", "content"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "the group Admin is not the configured Admin group");
    }

    [Fact]
    public async Task AdminEndpoint_TokenWithRoleClaimAdmin_IsAuthorisedAsAdmin()
    {
        await using var factory = CreateFactoryAsync();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Create(
            new Claim("sub", "service-account"),
            new Claim("name", "Service Account"),
            new Claim("role", "Admin")));

        var response = await client.PostAsJsonAsync("/api/posts", new CreatePostRequest("Role claim post", "content"));

        response.StatusCode.Should().Be(HttpStatusCode.Created, "role claims pass through unchanged");
    }

    [Fact]
    public async Task TokenWithConfiguredEditorGroup_IsAnEditor_NotAnAdmin()
    {
        // "Blog Editors" is configured (Oidc:RoleGroups:Editor) and, until now, never sent in any token:
        // the Editor side of the mapping was untested at the HTTP layer.
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Create(
            new Claim("sub", "user-9"), new Claim("name", "Draft Author"), new Claim("groups", "Blog Admins")));
        var created = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest("Editor group draft", "content"));
        created.EnsureSuccessStatusCode();
        var draft = (await created.Content.ReadFromJsonAsync<PostDto>())!;

        var editor = factory.CreateClient();
        editor.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Create(
            new Claim("sub", "user-9"), new Claim("name", "Draft Author"), new Claim("groups", "Blog Editors")));

        var update = await editor.PutAsJsonAsync($"/api/posts/{draft.Id}", new UpdatePostRequest("Edited as an Editor", "edited"));
        var createPost = await editor.PostAsJsonAsync("/api/posts", new CreatePostRequest("Editors cannot create", "content"));
        var publish = await editor.PostAsJsonAsync($"/api/posts/{draft.Id}/publish", new PublishRequest());

        update.StatusCode.Should().Be(HttpStatusCode.OK, "the configured Editor group grants the Editor role: an Editor edits their own draft");
        createPost.StatusCode.Should().Be(HttpStatusCode.Forbidden, "an Editor is not an Admin");
        publish.StatusCode.Should().Be(HttpStatusCode.Forbidden, "publishing is the Admin's");
    }

    private BlogApiFactory CreateFactoryAsync()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);
        // The class fixture has already created and migrated the class database. The real
        // JwtBearer pipeline in Production, audience validation included: the tokens carry the
        // audience (TestTokens.Create), as the IdP's would.
        return JwtBearerApi.Create(
            fixture.ConnectionString,
            storagePath,
            configuration: new Dictionary<string, string?>
            {
                ["Oidc:RoleGroups:Admin"] = "Blog Admins",
                ["Oidc:RoleGroups:Editor"] = "Blog Editors"
            });
    }
}
