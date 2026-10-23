using System.Text.Json;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Croft.Web.Components.Pages;
using Croft.Web.Options;
using Croft.Web.Services;
using Bunit;
using Bunit.Rendering;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Croft.Web.Tests.Components;

/// <summary>
/// Brief 03e item 3 (pattern from item 1's SeoHead, applied to the category page the way item 2
/// applied it to Home): the category page's SEO head, observed exactly as a browser receives it
/// (a <see cref="HeadOutlet"/> rendered beside the page, like App.razor). The page describes
/// itself as "Posts in {category} on {blog}", canonicalises to /category/{slug} (?page=N beyond
/// page 1 canonicalises to itself) and carries og:type website plus the site's schema.org Blog
/// JSON-LD. Expected values are read from the fixture's Branding, so a hard-coded name or
/// base URL in src fails the tests.
/// </summary>
public class CategorySeoHeadTests : BlogBunitContext
{
    private string BlogName =>
        Services.GetRequiredService<Croft.Web.Branding>().Name;

    private string Tagline =>
        Services.GetRequiredService<Croft.Web.Branding>().Tagline;

    private string Origin =>
        Services.GetRequiredService<IOptions<BlogOptions>>().Value.BaseUrl.TrimEnd('/');

    /// <summary>The category the tests seed; its name flows into the page's description.</summary>
    private static readonly CategoryDto Technology =
        new(Guid.CreateVersion7(), "Technology", "technology", null, 11);

    [Fact]
    public async Task Category_EmitsWebsiteSeoHeadWithBlogJsonLd()
    {
        var cut = await RenderCategoryAsync(Technology, ElevenPosts());

        await cut.WaitForAssertionAsync(() =>
        {
            cut.Find("link[rel='canonical']").GetAttribute("href").Should().Be($"{Origin}/category/technology");
            cut.Find("meta[name='description']").GetAttribute("content")
                .Should().Be($"Posts in {Technology.Name} on {BlogName}");
            cut.Find("meta[property='og:type']").GetAttribute("content").Should().Be("website");
            cut.Find("meta[property='og:title']").GetAttribute("content").Should().Be(Technology.Name);
            cut.Find("meta[property='og:description']").GetAttribute("content")
                .Should().Be($"Posts in {Technology.Name} on {BlogName}");
            cut.Find("meta[property='og:url']").GetAttribute("content").Should().Be($"{Origin}/category/technology");
            cut.Find("meta[property='og:site_name']").GetAttribute("content").Should().Be(BlogName);
            cut.Find("meta[property='og:locale']").GetAttribute("content").Should().Be("en_GB");
            cut.Find("meta[name='twitter:card']").GetAttribute("content").Should().Be("summary");
            cut.FindAll("meta[name='robots']").Should().BeEmpty("the category listing is indexable");
            cut.FindAll("meta[property='og:image']").Should().BeEmpty("the listing has no image of its own");
            cut.FindAll("meta[property='article:published_time']").Should()
                .BeEmpty("article:* tags belong to post pages, not the listing");
        });

        JsonElement root = default;
        await cut.WaitForAssertionAsync(() =>
        {
            using var doc = JsonDocument.Parse(cut.Find("script[type='application/ld+json']").TextContent);
            root = doc.RootElement.Clone();
        });

        root.GetProperty("@context").GetString().Should().Be("https://schema.org");
        root.GetProperty("@type").GetString().Should().Be("Blog");
        root.GetProperty("name").GetString().Should().Be(BlogName);
        root.GetProperty("description").GetString().Should().Be(Tagline);
        root.GetProperty("url").GetString().Should().Be($"{Origin}/");
    }

    [Fact]
    public async Task Category_Page2_CanonicalizesToItself()
    {
        var cut = await RenderCategoryAsync(Technology, ElevenPosts());

        // A pager click is a query-only navigation: Blazor reuses the page instance, and without
        // a router in the tree bUnit needs the changed parameter set explicitly (same shape as
        // HomeSeoHeadTests / PageLoadRaceTests).
        Services.GetRequiredService<NavigationManager>().NavigateTo("/category/technology?page=2");
        var category = cut.FindComponent<Category>();
        category.Instance.PageNumber = "2";
        category.Render();

        await cut.WaitForAssertionAsync(() =>
        {
            cut.Find("link[rel='canonical']").GetAttribute("href").Should().Be($"{Origin}/category/technology?page=2");
            cut.Find("meta[property='og:url']").GetAttribute("content").Should().Be($"{Origin}/category/technology?page=2");
            cut.Markup.Should().Contain("Post 11", "page 2 is a real listing page, not just a URL");
        });
    }

    /// <summary>11 published posts in the category, so page 2 of the 10-per-page listing carries one
    /// post. The fake filters by CategorySlug like the API, so the posts carry the page's category.</summary>
    private static List<PostSummaryDto> ElevenPosts() =>
        [.. Enumerable.Range(1, 11).Select(n => Summary($"post-{n}", $"Post {n}"))];

    private static PostSummaryDto Summary(string slug, string title) =>
        new(Guid.CreateVersion7(), slug, title, null, null, "A summary.", 2, PostStatus.Published,
            new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc), "An Author", Technology, [], null, 4, 1);

    /// <summary>Renders the category page with a <see cref="HeadOutlet"/> beside it, exactly like
    /// App.razor, so the page's head output is observable in the rendered markup.</summary>
    private async Task<IRenderedComponent<ContainerFragment>> RenderCategoryAsync(
        CategoryDto category, List<PostSummaryDto> posts)
    {
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient { Categories = [category], Posts = posts });

        var cut = Render(builder =>
        {
            builder.OpenComponent<HeadOutlet>(0);
            builder.CloseComponent();
            builder.OpenComponent<Category>(1);
            builder.AddAttribute(2, "Slug", category.Slug);
            builder.CloseComponent();
        });

        // The category — and with it the head, whose description needs the category's name —
        // loads asynchronously; wait until the head output is present so callers can assert
        // synchronously (same shape as HomeSeoHeadTests).
        await cut.WaitForAssertionAsync(() => cut.Find("link[rel='canonical']").Should().NotBeNull());
        return cut;
    }
}
