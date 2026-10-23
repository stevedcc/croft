using Croft.Web.Components.Pages;
using Bunit;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests.Components;

/// <summary>
/// The Post page loads its post from the API once per slug and renders it. (Brief 09f: the page
/// is static SSR and never becomes interactive, so there is no prerender-to-interactive handoff
/// and no persisted state any more; these tests were PostPersistentStateTests before.)
/// </summary>
public class PostPageLoadTests : BlogBunitContext
{
    [Fact]
    public void Post_FetchesFromTheApiOnceAndRenders()
    {
        // Arrange: create a post in the fake
        var post = new Croft.Shared.Contracts.PostDto(
            Guid.NewGuid(),
            "test-post",
            "Test Post Title",
            null,
            "# Heading\n\nContent here.",
            null,
            null,
            Croft.Shared.Enums.PostStatus.Published,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            "author-id",
            "Test Author",
            null,
            [],
            null,
            5);

        var fake = Services.GetRequiredService<FakeBlogApiClient>();
        fake.PostsById[post.Id] = post;

        // Act: render the Post page without restored state
        var cut = Render<Post>(parameters => parameters.Add(p => p.Slug, "test-post"));

        // Assert: API was called
        fake.PostSlugRequests.Should().ContainSingle().Which.Should().Be("test-post",
            "without restored state, the page should fetch from the API");

        // Assert: the content renders
        cut.Markup.Should().Contain("Test Post Title", "the post's title should render");

        // Brief 09f item 4: the post's id rides on the layout, for the page-view beacon that
        // static-glue.js sends (the page has no circuit to send it from).
        cut.Find(".post-layout").GetAttribute("data-post-id").Should().Be(post.Id.ToString(),
            "static-glue.js reads the post id from data-post-id");
    }

    [Fact]
    public void Post_DifferentSlug_FetchesThatPost()
    {
        // Arrange: create two posts
        var postA = new Croft.Shared.Contracts.PostDto(
            Guid.NewGuid(),
            "post-a",
            "Post A Title",
            null,
            "# Post A",
            null,
            null,
            Croft.Shared.Enums.PostStatus.Published,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            "author-id",
            "Test Author",
            null,
            [],
            null,
            5);

        var postB = new Croft.Shared.Contracts.PostDto(
            Guid.NewGuid(),
            "post-b",
            "Post B Title",
            null,
            "# Post B",
            null,
            null,
            Croft.Shared.Enums.PostStatus.Published,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            "author-id",
            "Test Author",
            null,
            [],
            null,
            5);

        var fake = Services.GetRequiredService<FakeBlogApiClient>();
        fake.PostsById[postA.Id] = postA;
        fake.PostsById[postB.Id] = postB;

        // Act 1: render with slug A first
        var cut = Render<Post>(parameters => parameters.Add(p => p.Slug, "post-a"));

        // Assert 1: A's data is rendered
        cut.Markup.Should().Contain("Post A Title");

        // Act 2: change slug to B (simulates client-side navigation)
        cut.Render(parameters => parameters.Add(p => p.Slug, "post-b"));

        // Assert 2: B is fetched
        fake.PostSlugRequests.Should().Equal(["post-a", "post-b"]);
        cut.Markup.Should().Contain("Post B Title");
        cut.Markup.Should().NotContain("Post A Title");
    }
}
