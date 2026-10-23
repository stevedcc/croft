using Croft.Shared.Contracts;
using Croft.Web.Components.Pages;
using Croft.Web.Services;
using Bunit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests.Components;

/// <summary>
/// The categories directory behind the sidebar's "More categories" link: every category as a
/// link with its published-post count, paged on the server (?page=N like the other list pages).
/// </summary>
public class CategoriesPageTests : BlogBunitContext
{
    private static CategoryDto Category(int n) =>
        new(Guid.CreateVersion7(), $"Category {n:00}", $"category-{n:00}", null, n);

    private FakeBlogApiClient Setup(params CategoryDto[] categories)
    {
        var fake = new FakeBlogApiClient { Categories = [.. categories] };
        Services.AddSingleton<IBlogApiClient>(fake);
        return fake;
    }

    [Fact]
    public async Task Directory_ShowsOneEntryPerCategory_WithItsCountAndLink()
    {
        Setup(Category(3), Category(1));

        var cut = Render<Categories>();
        await cut.WaitForAssertionAsync(() => cut.FindAll(".category-directory__item").Should().HaveCount(2));

        string[] nameOrder = ["/category/category-01", "/category/category-03"];
        var links = cut.FindAll("a").Where(a => a.GetAttribute("href")?.StartsWith("/category/") == true).ToList();
        links.Select(a => a.GetAttribute("href")).Should().Equal(nameOrder,
            "the directory is name-ordered like the admin list");
        cut.Markup.Should().Contain("1 posts").And.Contain("3 posts",
            "each entry carries its published-post count");
    }

    [Fact]
    public async Task Directory_PagesOnTheServer_WithThePageQueryParameter()
    {
        Setup([.. Enumerable.Range(1, 30).Select(Category)]);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/categories?page=2");

        var cut = Render<Categories>();

        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("Category 30")
            .And.NotContain("Category 01", "page 2 shows the tail, not page 1 again"));
        cut.FindAll("a").Count(a => a.GetAttribute("href")?.StartsWith("/category/") == true)
            .Should().Be(6, "30 categories at 24 per page leave 6 for page 2");
        cut.Find(".page-pager").Should().NotBeNull("the pager links work without JavaScript, like the public lists");
    }

    [Fact]
    public async Task EmptyDirectory_ShowsTheEmptyState()
    {
        Setup();

        var cut = Render<Categories>();

        await cut.WaitForAssertionAsync(() =>
            cut.Find(".category-directory__empty").TextContent.Should().Be("No categories yet."));
    }
}
