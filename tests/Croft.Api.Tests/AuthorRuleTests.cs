using System.Net;
using AwesomeAssertions;
using Croft.Api.Tests.Fixtures;
using Croft.Shared.Contracts;

namespace Croft.Api.Tests;

/// <summary>
/// The two author-only rules, which need distinct users: an Editor may update only their own
/// draft, and a non-admin may delete only their own feedback (C-1 of the 2026-10-19 test-suite
/// review). Editor A against Editor B against the Admin; the refusals leave the row unchanged.
/// </summary>
public class AuthorRuleTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    [Fact]
    public async Task UpdatePost_TheDraftsAuthor_AsEditor_Succeeds()
    {
        await using var factory = CreateFactory();
        var post = await CreateDraftByAsync(factory, "editor-a");

        var response = await factory.CreateClient().WithUser("editor-a", "Editor")
            .PutAsJsonAsync($"/api/posts/{post.Id}", new UpdatePostRequest("Edited by its author", "new content"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await GetPostAsync(factory, post.Id);
        stored.Title.Should().Be("Edited by its author");
        stored.MarkdownContent.Should().Be("new content");
    }

    [Fact]
    public async Task UpdatePost_AnotherEditorsDraft_IsNotFound_AndTheRowIsUnchanged()
    {
        await using var factory = CreateFactory();
        var post = await CreateDraftByAsync(factory, "editor-a");
        var before = await GetPostAsync(factory, post.Id);

        var response = await factory.CreateClient().WithUser("editor-b", "Editor")
            .PutAsJsonAsync($"/api/posts/{post.Id}", new UpdatePostRequest("Edited by someone else", "hijacked"));

        // The service answers "no such post of yours" (null) rather than 403, so a non-author
        // cannot even learn the draft exists.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await GetPostAsync(factory, post.Id)).Should().BeEquivalentTo(before,
            "the other Editor's refused update changed nothing");
    }

    [Fact]
    public async Task UpdatePost_AnotherEditorsDraft_AsAdmin_Succeeds()
    {
        await using var factory = CreateFactory();
        var post = await CreateDraftByAsync(factory, "editor-a");

        var response = await factory.CreateClient().WithUser("the-admin", "Admin")
            .PutAsJsonAsync($"/api/posts/{post.Id}", new UpdatePostRequest("Edited by the admin", "admin content"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetPostAsync(factory, post.Id)).Title.Should().Be("Edited by the admin");
    }

    [Fact]
    public async Task UpdatePost_AuthorWhoIsNoLongerAnEditorOrAdmin_IsForbidden()
    {
        await using var factory = CreateFactory();
        var post = await CreateDraftByAsync(factory, "editor-a");
        var before = await GetPostAsync(factory, post.Id);

        var response = await factory.CreateClient().WithUser("editor-a", "")
            .PutAsJsonAsync($"/api/posts/{post.Id}", new UpdatePostRequest("Edited without a role", "content"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetPostAsync(factory, post.Id)).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task DeleteFeedback_TheAuthor_AsEditor_RemovesIt()
    {
        await using var factory = CreateFactory();
        var (post, feedback) = await CreatePostWithFeedbackAsync(factory, "editor-a");

        var response = await factory.CreateClient().WithUser("editor-a", "Editor")
            .DeleteAsync($"/api/posts/{post.Id}/feedback/{feedback.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ListFeedbackAsync(factory, post.Id)).Should().NotContain(f => f.Id == feedback.Id);
    }

    [Fact]
    public async Task DeleteFeedback_AnotherEditorsFeedback_IsNotFound_AndTheRowRemains()
    {
        await using var factory = CreateFactory();
        var (post, feedback) = await CreatePostWithFeedbackAsync(factory, "editor-a");

        var response = await factory.CreateClient().WithUser("editor-b", "Editor")
            .DeleteAsync($"/api/posts/{post.Id}/feedback/{feedback.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ListFeedbackAsync(factory, post.Id)).Should().ContainSingle(f => f.Id == feedback.Id,
            "the other Editor's refused delete left the feedback in place");
    }

    [Fact]
    public async Task DeleteFeedback_AnEditorsFeedback_AsAdmin_RemovesIt()
    {
        await using var factory = CreateFactory();
        var (post, feedback) = await CreatePostWithFeedbackAsync(factory, "editor-a");

        var response = await factory.CreateClient().WithUser("the-admin", "Admin")
            .DeleteAsync($"/api/posts/{post.Id}/feedback/{feedback.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ListFeedbackAsync(factory, post.Id)).Should().NotContain(f => f.Id == feedback.Id);
    }

    [Fact]
    public async Task DeleteFeedback_TheAuthor_ThroughAnotherPostsUrl_IsNotFound_AndTheRowRemains()
    {
        await using var factory = CreateFactory();
        var (post, feedback) = await CreatePostWithFeedbackAsync(factory, "editor-a");
        var otherPost = await CreateDraftByAsync(factory, "editor-a");
        var author = factory.CreateClient().WithUser("editor-a", "Editor");

        var wrongUrl = await author.DeleteAsync($"/api/posts/{otherPost.Id}/feedback/{feedback.Id}");

        wrongUrl.StatusCode.Should().Be(HttpStatusCode.NotFound, "the feedback does not belong to the post in the URL");
        (await ListFeedbackAsync(factory, post.Id)).Should().ContainSingle(f => f.Id == feedback.Id,
            "a delete through another post's URL removes nothing");

        var rightUrl = await author.DeleteAsync($"/api/posts/{post.Id}/feedback/{feedback.Id}");

        rightUrl.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ListFeedbackAsync(factory, post.Id)).Should().NotContain(f => f.Id == feedback.Id);
    }

    [Fact]
    public async Task DeleteFeedback_UnknownFeedback_IsNotFound()
    {
        await using var factory = CreateFactory();
        var (post, _) = await CreatePostWithFeedbackAsync(factory, "editor-a");

        var response = await factory.CreateClient().WithUser("the-admin", "Admin")
            .DeleteAsync($"/api/posts/{post.Id}/feedback/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>A draft whose author is <paramref name="authorSub"/> (created while that user was an Admin).</summary>
    private static async Task<PostDto> CreateDraftByAsync(BlogApiFactory factory, string authorSub)
    {
        var response = await factory.CreateClient().WithUser(authorSub, "Admin")
            .PostAsJsonAsync("/api/posts", new CreatePostRequest($"Draft by {authorSub} {Guid.NewGuid():N}", "original content"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PostDto>())!;
    }

    private static async Task<(PostDto Post, FeedbackDto Feedback)> CreatePostWithFeedbackAsync(
        BlogApiFactory factory, string feedbackAuthorSub)
    {
        var post = await CreateDraftByAsync(factory, "the-admin");
        var response = await factory.CreateClient().WithUser(feedbackAuthorSub, "Editor")
            .PostAsJsonAsync($"/api/posts/{post.Id}/feedback", new CreateFeedbackRequest("quoted", "a comment"));
        response.EnsureSuccessStatusCode();
        return (post, (await response.Content.ReadFromJsonAsync<FeedbackDto>())!);
    }

    private static async Task<PostDto> GetPostAsync(BlogApiFactory factory, Guid id)
    {
        // Admin list holds the draft; the by-slug read needs the slug, so find it through the list.
        var admin = factory.CreateClient().WithUser("the-admin", "Admin");
        var list = await admin.GetFromJsonAsync<PagedResult<PostSummaryDto>>("/api/posts?pageSize=100");
        var slug = list!.Items.Single(p => p.Id == id).Slug;
        return (await admin.GetFromJsonAsync<PostDto>($"/api/posts/{slug}"))!;
    }

    private static async Task<IReadOnlyList<FeedbackDto>> ListFeedbackAsync(BlogApiFactory factory, Guid postId) =>
        (await factory.CreateClient().WithUser("the-admin", "Admin")
            .GetFromJsonAsync<List<FeedbackDto>>($"/api/posts/{postId}/feedback"))!;

    private BlogApiFactory CreateFactory()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);
        return new BlogApiFactory(fixture.ConnectionString, storagePath);
    }
}
