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
/// Brief 03e item 4 (pattern from item 1's SeoHead, applied to the tag page the way item 3
/// applied it to the category page): the tag page's SEO head, observed exactly as a browser
/// receives it (a <see cref="HeadOutlet"/> rendered beside the page, like App.razor). The page
/// describes itself as "Posts tagged {tag} on {blog}", canonicalises to /tag/{slug} (?page=N
/// beyond page 1 canonicalises to itself) and carries og:type website plus the site's
/// schema.org Blog JSON-LD. Expected values are read from the fixture's Branding, so a
/// hard-coded name or base URL in src fails the tests. FakeBlogApiClient.GetPostsAsync filters by
/// TagSlug like the API, so the seeds carry the tag the page asks for.
/// </summary>
public class TagSeoHeadTests : BlogBunitContext
{
    private string BlogName =>
        Services.GetRequiredService<Croft.Web.Branding>().Name;

    private string Tagline =>
        Services.GetRequiredService<Croft.Web.Branding>().Tagline;

    private string Origin =>
        Services.GetRequiredService<IOptions<BlogOptions>>().Value.BaseUrl.TrimEnd('/');

    /// <summary>The tag the tests seed; its name flows into the page's description.</summary>
    private static readonly TagDto Blazor =
        new(Guid.CreateVersion7(), "Blazor", "blazor", 11, true);

    [Fact]
    public async Task Tag_EmitsWebsiteSeoHeadWithBlogJsonLd()
    {
        var cut = await RenderTagAsync(Blazor, ElevenPosts());

        await cut.WaitForAssertionAsync(() =>
        {
            cut.Find("link[rel='canonical']").GetAttribute("href").Should().Be($"{Origin}/tag/blazor");
            cut.Find("meta[name='description']").GetAttribute("content")
                .Should().Be($"Posts tagged {Blazor.Name} on {BlogName}");
            cut.Find("meta[property='og:type']").GetAttribute("content").Should().Be("website");
            cut.Find("meta[property='og:title']").GetAttribute("content").Should().Be(Blazor.Name);
            cut.Find("meta[property='og:description']").GetAttribute("content")
                .Should().Be($"Posts tagged {Blazor.Name} on {BlogName}");
            cut.Find("meta[property='og:url']").GetAttribute("content").Should().Be($"{Origin}/tag/blazor");
            cut.Find("meta[property='og:site_name']").GetAttribute("content").Should().Be(BlogName);
            cut.Find("meta[property='og:locale']").GetAttribute("content").Should().Be("en_GB");
            cut.Find("meta[name='twitter:card']").GetAttribute("content").Should().Be("summary");
            cut.FindAll("meta[name='robots']").Should().BeEmpty("the tag listing is indexable");
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
    public async Task Tag_Page2_CanonicalizesToItself()
    {
        var cut = await RenderTagAsync(Blazor, ElevenPosts());

        // A pager click is a query-only navigation: Blazor reuses the page instance, and without
        // a router in the tree bUnit needs the changed parameter set explicitly (same shape as
        // HomeSeoHeadTests / PageLoadRaceTests).
        Services.GetRequiredService<NavigationManager>().NavigateTo("/tag/blazor?page=2");
        var tag = cut.FindComponent<Tag>();
        tag.Instance.PageNumber = "2";
        tag.Render();

        await cut.WaitForAssertionAsync(() =>
        {
            cut.Find("link[rel='canonical']").GetAttribute("href").Should().Be($"{Origin}/tag/blazor?page=2");
            cut.Find("meta[property='og:url']").GetAttribute("content").Should().Be($"{Origin}/tag/blazor?page=2");
            cut.Markup.Should().Contain("Post 11", "page 2 is a real listing page, not just a URL");
        });
    }

    /// <summary>11 published posts carrying the tag, so page 2 of the 10-per-page listing carries one
    /// post. The fake filters by TagSlug like the API, so the posts carry the tag the page asks for.</summary>
    private static List<PostSummaryDto> ElevenPosts() =>
        [.. Enumerable.Range(1, 11).Select(n => Summary($"post-{n}", $"Post {n}"))];

    private static PostSummaryDto Summary(string slug, string title) =>
        new(Guid.CreateVersion7(), slug, title, null, null, "A summary.", 2, PostStatus.Published,
            new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc), "An Author", null, [Blazor], null, 4, 1);

    /// <summary>Renders the tag page with a <see cref="HeadOutlet"/> beside it, exactly like
    /// App.razor, so the page's head output is observable in the rendered markup.</summary>
    private async Task<IRenderedComponent<ContainerFragment>> RenderTagAsync(
        TagDto tag, List<PostSummaryDto> posts)
    {
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient { Tags = [tag], Posts = posts });

        var cut = Render(builder =>
        {
            builder.OpenComponent<HeadOutlet>(0);
            builder.CloseComponent();
            builder.OpenComponent<Tag>(1);
            builder.AddAttribute(2, "Slug", tag.Slug);
            builder.CloseComponent();
        });

        // The tag — and with it the head, whose description needs the tag's name — loads
        // asynchronously; wait until the head output is present so callers can assert
        // synchronously (same shape as HomeSeoHeadTests).
        await cut.WaitForAssertionAsync(() => cut.Find("link[rel='canonical']").Should().NotBeNull());
        return cut;
    }
}
