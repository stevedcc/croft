using Bunit;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Croft.Web.Components.Pages;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests.Components;

/// <summary>
/// Parameter-change tests for I-2: verify that when the Slug parameter of a rendered page changes, it fetches
/// and shows the data of the new slug (and nothing of the old one).
/// </summary>
public class ParameterChangeTests : BlogBunitContext
{
    [Fact]
    public void Category_WithDifferentSlugs_FetchesCorrectCategory()
    {
        // Arrange
        var fake = Services.GetRequiredService<FakeBlogApiClient>();
        var catA = new CategoryDto(Guid.NewGuid(), "Category A", "cat-a", "icon-a", 5);
        var catB = new CategoryDto(Guid.NewGuid(), "Category B", "cat-b", "icon-b", 3);
        fake.Categories.Add(catA);
        fake.Categories.Add(catB);

        // Act: Render with slug A, then change the parameter on that same instance (a route change
        // between two slugs reuses the page instance: two separate renders would be two first renders)
        var cut = Render<Category>(parameters => parameters.Add(p => p.Slug, "cat-a"));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Category A"));

        cut.Render(parameters => parameters.Add(p => p.Slug, "cat-b"));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Category B"));
        cut.Markup.Should().NotContain("Category A", "the page shows the new slug's content, not the old one's");

        // Assert: Both categories were fetched
        fake.CategorySlugRequests.Should().Equal("cat-a", "cat-b");
    }

    [Fact]
    public void Tag_WithDifferentSlugs_FetchesCorrectTag()
    {
        // Arrange
        var fake = Services.GetRequiredService<FakeBlogApiClient>();
        var tagA = new TagDto(Guid.NewGuid(), "Tag A", "tag-a", 5, true);
        var tagB = new TagDto(Guid.NewGuid(), "Tag B", "tag-b", 3, true);
        fake.Tags.Add(tagA);
        fake.Tags.Add(tagB);

        // Act: Render with slug A, then change the parameter on that same instance (a route change
        // between two slugs reuses the page instance: two separate renders would be two first renders)
        var cut = Render<Tag>(parameters => parameters.Add(p => p.Slug, "tag-a"));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Tag A"));

        cut.Render(parameters => parameters.Add(p => p.Slug, "tag-b"));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Tag B"));
        cut.Markup.Should().NotContain("Tag A", "the page shows the new slug's content, not the old one's");

        // Assert: Both tags were fetched
        fake.TagSlugRequests.Should().Equal("tag-a", "tag-b");
    }

    [Fact]
    public void StaticPage_WithDifferentSlugs_FetchesCorrectPage()
    {
        // Arrange
        var fake = Services.GetRequiredService<FakeBlogApiClient>();
        var pageA = new PostDto(Guid.NewGuid(), "page-a", "Page A", null, "# Page A", null, null,
            PostStatus.Published, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            "author-id", "Author", null, [], null, 5);
        var pageB = new PostDto(Guid.NewGuid(), "page-b", "Page B", null, "# Page B", null, null,
            PostStatus.Published, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            "author-id", "Author", null, [], null, 5);
        fake.Pages.Add(pageA);
        fake.Pages.Add(pageB);

        // Act: Render with slug A, then change the parameter on that same instance (a route change
        // between two slugs reuses the page instance: two separate renders would be two first renders)
        var cut = Render<StaticPage>(parameters => parameters.Add(p => p.Slug, "page-a"));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Page A"));

        cut.Render(parameters => parameters.Add(p => p.Slug, "page-b"));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Page B"));
        cut.Markup.Should().NotContain("Page A", "the page shows the new slug's content, not the old one's");

        // Assert: Both pages were fetched
        fake.PageSlugRequests.Should().Equal("page-a", "page-b");
    }

    [Fact]
    public void Post_WithDifferentSlugs_FetchesCorrectPost()
    {
        // Arrange
        var fake = Services.GetRequiredService<FakeBlogApiClient>();
        var postA = new PostDto(Guid.NewGuid(), "post-a", "Post A", null, "# Post A", null, null,
            PostStatus.Published, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            "author-id", "Author", null, [], null, 5);
        var postB = new PostDto(Guid.NewGuid(), "post-b", "Post B", null, "# Post B", null, null,
            PostStatus.Published, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            "author-id", "Author", null, [], null, 5);
        fake.PostsById[postA.Id] = postA;
        fake.PostsById[postB.Id] = postB;

        // Act: Render with slug A, then change the parameter on that same instance (a route change
        // between two slugs reuses the page instance: two separate renders would be two first renders)
        var cut = Render<Post>(parameters => parameters.Add(p => p.Slug, "post-a"));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Post A"));

        cut.Render(parameters => parameters.Add(p => p.Slug, "post-b"));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Post B"));
        cut.Markup.Should().NotContain("Post A", "the page shows the new slug's content, not the old one's");

        // Assert: Both posts were fetched
        fake.PostSlugRequests.Should().Equal("post-a", "post-b");
    }
}
