using Croft.Api.Tests.Fixtures;
using Croft.Shared.Contracts;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using System.Net;

namespace Croft.Api.Tests;

public class FeedbackControllerTests(PostgresDatabaseFixture fixture)
    : IClassFixture<PostgresDatabaseFixture>
{
    [Fact]
    public async Task EditorCanAddFeedback_WithQuotedText()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var postResponse = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest("Draft", "content"));
        postResponse.EnsureSuccessStatusCode();
        var post = await postResponse.Content.ReadFromJsonAsync<PostDto>();

        var editor = factory.CreateClient().WithRole("Editor");
        var create = await editor.PostAsJsonAsync(
            $"/api/posts/{post!.Id}/feedback",
            new CreateFeedbackRequest("selected text", "This needs clarification."));
        create.EnsureSuccessStatusCode();

        var list = await editor.GetAsync($"/api/posts/{post.Id}/feedback");
        list.EnsureSuccessStatusCode();
        var feedback = await list.Content.ReadFromJsonAsync<List<FeedbackDto>>();

        feedback!.Should().ContainSingle(f => f.QuotedText == "selected text" && f.Comment == "This needs clarification.");
    }

    [Fact]
    public async Task FeedbackOnMissingPost_ReturnsNotFound()
    {
        await using var factory = CreateFactoryAsync();
        var editor = factory.CreateClient().WithRole("Editor");

        var response = await editor.PostAsJsonAsync(
            $"/api/posts/{Guid.NewGuid()}/feedback",
            new CreateFeedbackRequest("text", "comment"));

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Resolve_SetsResolvedAt()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var postResponse = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest("Draft", "content"));
        postResponse.EnsureSuccessStatusCode();
        var post = await postResponse.Content.ReadFromJsonAsync<PostDto>();

        var editor = factory.CreateClient().WithRole("Editor");
        var create = await editor.PostAsJsonAsync(
            $"/api/posts/{post!.Id}/feedback",
            new CreateFeedbackRequest("selected text", "Please clarify this passage."));
        create.EnsureSuccessStatusCode();
        var feedback = await create.Content.ReadFromJsonAsync<FeedbackDto>();

        var resolve = await admin.PostAsync($"/api/posts/{post.Id}/feedback/{feedback!.Id}/resolve", null);
        resolve.EnsureSuccessStatusCode();
        var resolved = await resolve.Content.ReadFromJsonAsync<FeedbackDto>();

        resolved!.Id.Should().Be(feedback.Id);
        resolved.ResolvedAt.Should().NotBeNull();

        var list = await editor.GetAsync($"/api/posts/{post.Id}/feedback");
        list.EnsureSuccessStatusCode();
        var items = await list.Content.ReadFromJsonAsync<List<FeedbackDto>>();
        items!.Single(f => f.Id == feedback.Id).ResolvedAt.Should().NotBeNull(
            "the resolution must persist, not just be echoed back");
    }

    [Fact]
    public async Task Resolve_FeedbackOnOtherPost_Returns404()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var firstResponse = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest("First", "content"));
        firstResponse.EnsureSuccessStatusCode();
        var first = await firstResponse.Content.ReadFromJsonAsync<PostDto>();
        var secondResponse = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest("Second", "content"));
        secondResponse.EnsureSuccessStatusCode();
        var second = await secondResponse.Content.ReadFromJsonAsync<PostDto>();

        var editor = factory.CreateClient().WithRole("Editor");
        var create = await editor.PostAsJsonAsync(
            $"/api/posts/{first!.Id}/feedback",
            new CreateFeedbackRequest("selected text", "On the first post."));
        create.EnsureSuccessStatusCode();
        var feedback = await create.Content.ReadFromJsonAsync<FeedbackDto>();

        var resolve = await admin.PostAsync($"/api/posts/{second!.Id}/feedback/{feedback!.Id}/resolve", null);

        resolve.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var list = await editor.GetAsync($"/api/posts/{first.Id}/feedback");
        list.EnsureSuccessStatusCode();
        var items = await list.Content.ReadFromJsonAsync<List<FeedbackDto>>();
        items!.Single(f => f.Id == feedback.Id).ResolvedAt.Should().BeNull(
            "feedback on another post must not be resolved");
    }

    [Theory]
    [InlineData(2001, "ok comment", "Quote")]
    [InlineData(10, null, "Comment")]
    public async Task AddFeedback_OverLongText_Returns400WithAMessage(int quoteLength, string? comment, string field)
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await (await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest("Draft", "content")))
            .Content.ReadFromJsonAsync<PostDto>();
        var request = comment is null
            ? new CreateFeedbackRequest(new string('q', quoteLength), new string('c', 10_001))
            : new CreateFeedbackRequest(new string('q', quoteLength), comment);

        var response = await admin.PostAsJsonAsync($"/api/posts/{post!.Id}/feedback", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Detail.Should().Contain(field);
        (await admin.GetFromJsonAsync<List<FeedbackDto>>($"/api/posts/{post.Id}/feedback"))
            .Should().BeEmpty("a refused note is not stored");
    }

    [Fact]
    public async Task AddFeedback_AtTheLimits_IsAccepted()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await (await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest("Draft", "content")))
            .Content.ReadFromJsonAsync<PostDto>();

        var response = await admin.PostAsJsonAsync($"/api/posts/{post!.Id}/feedback",
            new CreateFeedbackRequest(new string('q', 2000), new string('c', 10_000)));

        response.EnsureSuccessStatusCode();
    }

    private BlogApiFactory CreateFactoryAsync()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);
        // The class fixture has already created and migrated the class database.
        return new BlogApiFactory(fixture.ConnectionString, storagePath);
    }
}
