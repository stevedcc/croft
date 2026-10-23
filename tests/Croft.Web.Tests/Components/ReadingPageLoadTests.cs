using Croft.Web.Components.Pages;
using Bunit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests.Components;

public class HomePageLoadTests : BlogBunitContext
{
    [Fact]
    public void Home_FetchesFromTheApiOnceAndRenders()
    {
        var fake = Services.GetRequiredService<FakeBlogApiClient>();
        fake.Posts.Add(new(Guid.NewGuid(), "post-1", "Post 1", null, null, "Summary", 5,
            Croft.Shared.Enums.PostStatus.Published, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), "Author", null, [], null, null, null));

        var cut = Render<Home>();

        fake.PostRequests.Should().ContainSingle();
        cut.Markup.Should().Contain("Post 1");
    }
}

public class CategoryPageLoadTests : BlogBunitContext
{
    [Fact]
    public void Category_FetchesFromTheApiOnceAndRenders()
    {
        var fake = Services.GetRequiredService<FakeBlogApiClient>();
        fake.Categories.Add(new(Guid.NewGuid(), "Programming", "programming", "Code", 3));

        var cut = Render<Category>(parameters => parameters.Add(p => p.Slug, "programming"));

        cut.WaitForAssertion(() => fake.CategorySlugRequests.Should().ContainSingle());
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Programming"));
    }
}

public class TagPageLoadTests : BlogBunitContext
{
    [Fact]
    public void Tag_FetchesFromTheApiOnceAndRenders()
    {
        var fake = Services.GetRequiredService<FakeBlogApiClient>();
        fake.Tags.Add(new(Guid.NewGuid(), "C#", "c-sharp", 5, true));

        var cut = Render<Tag>(parameters => parameters.Add(p => p.Slug, "c-sharp"));

        cut.WaitForAssertion(() => fake.TagSlugRequests.Should().ContainSingle());
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("C#"));
    }
}

public class CategoriesPageLoadTests : BlogBunitContext
{
    [Fact]
    public void Categories_FetchesFromTheApiOnceAndRenders()
    {
        var fake = Services.GetRequiredService<FakeBlogApiClient>();
        fake.Categories.Add(new(Guid.NewGuid(), "Programming", "programming", "Code", 3));

        var cut = Render<Categories>();

        cut.WaitForAssertion(() => fake.CategoryPageRequests.Should().ContainSingle());
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Programming"));
    }
}

public class StaticPagePageLoadTests : BlogBunitContext
{
    [Fact]
    public void StaticPage_FetchesFromTheApiOnceAndRenders()
    {
        var fake = Services.GetRequiredService<FakeBlogApiClient>();
        fake.Pages.Add(new Croft.Shared.Contracts.PostDto(
            Guid.NewGuid(), "about", "About", null, "# About", null, null,
            Croft.Shared.Enums.PostStatus.Published,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            "author-id", "Author", null, [], null, 5));

        var cut = Render<StaticPage>(parameters => parameters.Add(p => p.Slug, "about"));

        cut.WaitForAssertion(() => fake.PageSlugRequests.Should().ContainSingle());
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("About"));
    }
}

public class SearchPageLoadTests : BlogBunitContext
{
    [Fact]
    public void Search_FetchesFromTheApiOnceAndRenders()
    {
        var fake = Services.GetRequiredService<FakeBlogApiClient>();
        fake.Posts.Add(new(Guid.NewGuid(), "post-1", "Test Post", null, null, "Summary", 5,
            Croft.Shared.Enums.PostStatus.Published, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), "Author", null, [], null, null, null));

        // Navigate to the search page with query parameter
        var nav = Services.GetRequiredService<NavigationManager>();
        var uri = nav.GetUriWithQueryParameter("q", "test");
        nav.NavigateTo(uri.ToString());

        var cut = Render<Search>();

        cut.WaitForAssertion(() => fake.SearchRequests.Should().ContainSingle());
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Test Post"));
    }
}
