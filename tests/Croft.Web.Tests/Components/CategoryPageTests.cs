using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Croft.Web.Components.Pages;
using Croft.Web.Services;
using Bunit;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests.Components;

/// <summary>
/// The category page resolves its slug with one lookup: loading the whole categories table into
/// the browser just to find one row does not scale to thousands of categories.
/// </summary>
public class CategoryPageTests : BlogBunitContext
{
    [Fact]
    public async Task CategoryPage_ResolvesTheSlugWithOneLookup_NeverTheWholeTable()
    {
        var fake = new FakeBlogApiClient
        {
            Categories = [new(Guid.CreateVersion7(), "Programming", "programming", "Code", 3)],
        };
        Services.AddSingleton<IBlogApiClient>(fake);

        var cut = Render<Category>(p => p.Add(x => x.Slug, "programming"));

        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("Programming", "the header shows the resolved category"));
        string[] oneLookup = ["programming"];
        fake.CategorySlugRequests.Should().Equal(oneLookup,
            "the page looks the one category up by slug");
        fake.CategoryPageRequests.Should().BeEmpty("no category listing is fetched for a single-category page");
    }

    [Fact]
    public async Task CategoryPage_AsksForThePublicView_WhoeverIsSignedIn()
    {
        var fake = new FakeBlogApiClient { Categories = [new(Guid.CreateVersion7(), "Programming", "programming", "Code", 3)] };
        Services.AddSingleton<IBlogApiClient>(fake);

        var cut = Render<Category>(p => p.Add(x => x.Slug, "programming"));

        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("Programming"));
        fake.PostRequests.Should().NotBeEmpty().And.OnlyContain(r => r.PublicView);
    }

    [Fact]
    public async Task CategoryPage_ListsOnlyThePostsInThatCategory_AndAsksTheApiForTheCategorysPosts()
    {
        var programming = new CategoryDto(Guid.CreateVersion7(), "Programming", "programming", "Code", 1);
        var travel = new CategoryDto(Guid.CreateVersion7(), "Travel", "travel", "Flight", 1);
        var fake = new FakeBlogApiClient
        {
            Categories = [programming, travel],
            Posts =
            [
                Summary("in-programming", "In Programming", programming),
                Summary("in-travel", "In Travel", travel),
                Summary("in-nothing", "In Nothing", null),
            ],
        };
        Services.AddSingleton<IBlogApiClient>(fake);

        var cut = Render<Category>(p => p.Add(x => x.Slug, "programming"));

        await cut.WaitForAssertionAsync(() => cut.FindAll(".post-card").Should().ContainSingle());
        cut.Find(".post-card__link").GetAttribute("href").Should().Be("/post/in-programming");
        cut.Markup.Should().NotContain("In Travel").And.NotContain("In Nothing");
        fake.PostRequests.Should().NotBeEmpty().And.OnlyContain(r => r.CategorySlug == "programming",
            "the page asks the API for this category's posts, not for the whole listing");
    }

    [Theory]
    [InlineData(1, "· 1 post")]
    [InlineData(2, "· 2 posts")]
    public async Task CategoryPage_HeaderCountsPostsInTheSingular_WhenThereIsOne(int count, string expected)
    {
        var category = new CategoryDto(Guid.CreateVersion7(), "Programming", "programming", "Code", count);
        var fake = new FakeBlogApiClient
        {
            Categories = [category],
            Posts = [.. Enumerable.Range(1, count).Select(i => Summary($"post-{i}", $"Post {i}", category))],
        };
        Services.AddSingleton<IBlogApiClient>(fake);

        var cut = Render<Category>(p => p.Add(x => x.Slug, "programming"));

        await cut.WaitForAssertionAsync(() => cut.Find(".page-header__meta").TextContent.Should().Be(expected));
    }

    private static PostSummaryDto Summary(string slug, string title, CategoryDto? category) =>
        new(Guid.CreateVersion7(), slug, title, null, null, "A summary.", 2, PostStatus.Published,
            new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc),
            "An Author", category, [], null, null, null);
}
