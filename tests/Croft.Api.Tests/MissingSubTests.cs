using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Croft.Api.Data;
using Croft.Api.Models;
using Croft.Api.Tests.Fixtures;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Croft.Api.Tests;

/// <summary>
/// Brief 13b item 7 (review finding 12): a token without a <c>sub</c> claim is refused (401) by the
/// API's authentication itself, so no endpoint ever sees such a principal. Before, every such caller
/// became the literal user "unknown": all Editors were the same author, each able to edit every
/// other's draft and delete every other's feedback. The tests run the real JwtBearer pipeline.
/// </summary>
public class MissingSubTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private const string AdminGroup = "Blog Admins";
    private const string EditorGroup = "Blog Editors";

    [Fact]
    public async Task AdminTokenWithoutSub_CannotCreateAPost_AndNothingIsWritten()
    {
        await using var factory = CreateFactory();
        var client = Client(factory, AdminGroup, sub: null);

        var response = await client.PostAsJsonAsync("/api/posts", new CreatePostRequest("No sub post", "content"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await CountAsync(factory, db => db.Posts.CountAsync(p => p.Title == "No sub post"))).Should().Be(0);
    }

    [Fact]
    public async Task EditorTokenWithoutSub_CannotEditADraftAuthoredByUnknown_TheOldSharedIdentity()
    {
        await using var factory = CreateFactory();
        var draft = await SeedDraftAsync(factory, authorId: "unknown");
        var client = Client(factory, EditorGroup, sub: null);

        var response = await client.PutAsJsonAsync($"/api/posts/{draft.Id}", new UpdatePostRequest("Hijacked", "hijacked"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a token without sub is refused before any ownership check can treat it as the author 'unknown'");
        (await CountAsync(factory, db => db.Posts.CountAsync(p => p.Id == draft.Id && p.Title == "Seeded draft"))).Should().Be(1);
    }

    [Fact]
    public async Task EditorTokenWithoutSub_CannotAddOrDeleteFeedback()
    {
        await using var factory = CreateFactory();
        var draft = await SeedDraftAsync(factory, authorId: "someone");
        var feedbackId = await SeedFeedbackAsync(factory, draft.Id, authorId: "unknown");
        var client = Client(factory, EditorGroup, sub: null);

        var create = await client.PostAsJsonAsync($"/api/posts/{draft.Id}/feedback", new CreateFeedbackRequest("quoted", "comment"));
        var delete = await client.DeleteAsync($"/api/posts/{draft.Id}/feedback/{feedbackId}");

        create.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        delete.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await CountAsync(factory, db => db.Feedback.CountAsync(f => f.PostId == draft.Id))).Should().Be(1,
            "nothing was added and the seeded feedback is still there");
    }

    [Fact]
    public async Task AdminTokenWithoutSub_CannotUploadMedia_AndNothingIsWritten()
    {
        await using var factory = CreateFactory();
        var client = Client(factory, AdminGroup, sub: null);
        var before = await CountAsync(factory, db => db.MediaAssets.CountAsync());
        using var content = new MultipartFormDataContent
        {
            { PngContent(), "file", "no-sub.png" },
            { new StringContent("global"), "scope" },
        };

        var response = await client.PostAsync("/api/media", content);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await CountAsync(factory, db => db.MediaAssets.CountAsync())).Should().Be(before);
    }

    [Fact]
    public async Task TheSameCallsWithASub_Work()
    {
        // The control: the 401s above come from the missing sub, not from the setup.
        await using var factory = CreateFactory();
        var admin = Client(factory, AdminGroup, sub: "admin-1");

        var created = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest("With sub post", "content"));
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var post = (await created.Content.ReadFromJsonAsync<PostDto>())!;

        var editor = Client(factory, EditorGroup, sub: "editor-1");
        var feedback = await editor.PostAsJsonAsync($"/api/posts/{post.Id}/feedback", new CreateFeedbackRequest("quoted", "comment"));
        feedback.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task ARefusedTokenWithoutSub_IsLoggedAsAWarning_OncePerIssuer()
    {
        var logs = new TestLogCapture();
        await using var baseFactory = CreateFactory();
        using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureLogging(l => l.AddProvider(logs)));
        var client = Client(factory, AdminGroup, sub: null);

        for (var n = 0; n < 3; n++)
        {
            (await client.PostAsJsonAsync("/api/posts", new CreatePostRequest("Log probe", "content"))).StatusCode
                .Should().Be(HttpStatusCode.Unauthorized);
        }

        logs.Entries.Where(e => e.Level == LogLevel.Warning && e.Message.Contains("carries no sub claim") && e.Message.Contains(TestTokens.Issuer))
            .Should().ContainSingle("the refusal is logged once per issuer, not once per request");
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory, string group, string? sub)
    {
        var claims = new List<Claim> { new("name", "Test User"), new("groups", group) };
        if (sub is not null)
        {
            claims.Add(new Claim("sub", sub));
        }

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create([.. claims]));
        return client;
    }

    private static async Task<Post> SeedDraftAsync(WebApplicationFactory<Program> factory, string authorId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        var now = DateTime.UtcNow;
        var post = new Post
        {
            Slug = $"seeded-{Guid.NewGuid():N}",
            Title = "Seeded draft",
            MarkdownContent = "seeded",
            Status = PostStatus.Draft,
            AuthorId = authorId,
            AuthorName = "Someone",
            CreatedAt = now,
            UpdatedAt = now,
            ContentEditedAt = now,
        };
        db.Posts.Add(post);
        await db.SaveChangesAsync();
        return post;
    }

    private static async Task<Guid> SeedFeedbackAsync(WebApplicationFactory<Program> factory, Guid postId, string authorId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        var feedback = new Feedback
        {
            PostId = postId,
            AuthorId = authorId,
            AuthorName = "Someone",
            QuotedText = "seeded quote",
            Comment = "seeded comment",
            CreatedAt = DateTime.UtcNow,
        };
        db.Feedback.Add(feedback);
        await db.SaveChangesAsync();
        return feedback.Id;
    }

    private static async Task<int> CountAsync(WebApplicationFactory<Program> factory, Func<BlogDbContext, Task<int>> count)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await count(scope.ServiceProvider.GetRequiredService<BlogDbContext>());
    }

    private static ByteArrayContent PngContent()
    {
        var content = new ByteArrayContent(Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return content;
    }

    private BlogApiFactory CreateFactory() =>
        JwtBearerApi.Create(
            fixture.ConnectionString,
            Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}"),
            configuration: new Dictionary<string, string?>
            {
                ["Oidc:RoleGroups:Admin"] = AdminGroup,
                ["Oidc:RoleGroups:Editor"] = EditorGroup,
            });
}
