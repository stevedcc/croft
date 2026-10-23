using System.Text.Json;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Croft.Web.Components.Layout;
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
/// Brief 03e item 2 (pattern from item 1's SeoHead): the home page's SEO head, observed exactly
/// as a browser receives it (a <see cref="HeadOutlet"/> rendered beside the page, like App.razor).
/// Page 1 describes the site with its description (the tagline when none is configured),
/// canonicalises to the site root and
/// carries og:type website plus a schema.org Blog JSON-LD (name, description, url); ?page=N
/// beyond page 1 canonicalises to itself. Expected values are read from the fixture's
/// Branding (name, words) and BlogOptions (base URL), so a hard-coded name, tagline or base
/// URL in src fails the tests. The Blog
/// JSON-LD entity is site-level in every field (controller ruling on item 2's review): its
/// description is the configured Tagline even when the emitting page's own Description —
/// which still reaches meta/og:description — differs.
/// </summary>
public class HomeSeoHeadTests : BlogBunitContext
{
    private string BlogName =>
        Services.GetRequiredService<Croft.Web.Branding>().Name;

    private string Tagline =>
        Services.GetRequiredService<Croft.Web.Branding>().Tagline;

    private string Origin =>
        Services.GetRequiredService<IOptions<BlogOptions>>().Value.BaseUrl.TrimEnd('/');

    [Fact]
    public async Task Home_EmitsWebsiteSeoHeadWithBlogJsonLd()
    {
        var cut = await RenderHomeAsync(ElevenPosts());

        await cut.WaitForAssertionAsync(() =>
        {
            cut.Find("link[rel='canonical']").GetAttribute("href").Should().Be($"{Origin}/");
            cut.Find("meta[name='description']").GetAttribute("content").Should().Be(Tagline);
            cut.Find("meta[property='og:type']").GetAttribute("content").Should().Be("website");
            cut.Find("meta[property='og:title']").GetAttribute("content").Should().Be(BlogName);
            cut.Find("meta[property='og:description']").GetAttribute("content").Should().Be(Tagline);
            cut.Find("meta[property='og:url']").GetAttribute("content").Should().Be($"{Origin}/");
            cut.Find("meta[property='og:site_name']").GetAttribute("content").Should().Be(BlogName);
            cut.Find("meta[property='og:locale']").GetAttribute("content").Should().Be("en_GB");
            cut.Find("meta[name='twitter:card']").GetAttribute("content").Should().Be("summary");
            cut.FindAll("meta[name='robots']").Should().BeEmpty("the home page is indexable");
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
    public async Task Home_DescribesWithTheDescription_WhenTheBrandingHasOne()
    {
        Services.AddSingleton(new Croft.Web.Branding
        {
            Name = "Test Blog",
            Tagline = "Testing, one line at a time",
            Description = "Notes from the field.",
        });

        var cut = await RenderHomeAsync(ElevenPosts());

        await cut.WaitForAssertionAsync(() =>
        {
            cut.Find("meta[name='description']").GetAttribute("content").Should().Be("Notes from the field.",
                "the home page is described by the site's description when one is configured");
            cut.Find("meta[property='og:description']").GetAttribute("content").Should().Be("Notes from the field.");
        });
    }

    [Fact]
    public async Task Home_Page2_CanonicalizesToItself()
    {
        var cut = await RenderHomeAsync(ElevenPosts());

        // A pager click is a query-only navigation: Blazor reuses the page instance, and without
        // a router in the tree bUnit needs the changed parameter set explicitly (same shape as
        // PageLoadRaceTests).
        Services.GetRequiredService<NavigationManager>().NavigateTo("/?page=2");
        var home = cut.FindComponent<Home>();
        home.Instance.PageNumber = "2";
        home.Render();

        await cut.WaitForAssertionAsync(() =>
        {
            cut.Find("link[rel='canonical']").GetAttribute("href").Should().Be($"{Origin}/?page=2");
            cut.Find("meta[property='og:url']").GetAttribute("content").Should().Be($"{Origin}/?page=2");
            cut.Markup.Should().Contain("Post 11", "page 2 is a real listing page, not just a URL");
        });
    }

    [Fact]
    public async Task WebsiteJsonLd_DescribesTheSiteWithTheTagline_EvenWhenPageDescriptionDiffers()
    {
        // Home always passes the tagline, so the site-level Blog entity's description source is
        // only observable when the page's own Description differs: meta/og:description carry the
        // page's value, the JSON-LD Blog stays described by the configured tagline.
        var cut = Render(builder =>
        {
            builder.OpenComponent<HeadOutlet>(0);
            builder.CloseComponent();
            builder.OpenComponent<SeoHead>(1);
            builder.AddAttribute(2, "Title", "A listing page");
            builder.AddAttribute(3, "Description", "The listing page's own description.");
            builder.AddAttribute(4, "CanonicalPath", "/?page=2");
            builder.AddAttribute(5, "PageType", SeoPageType.Website);
            builder.CloseComponent();
        });

        await cut.WaitForAssertionAsync(() =>
        {
            cut.Find("meta[name='description']").GetAttribute("content")
                .Should().Be("The listing page's own description.");
            cut.Find("meta[property='og:description']").GetAttribute("content")
                .Should().Be("The listing page's own description.");
        });

        JsonElement root = default;
        await cut.WaitForAssertionAsync(() =>
        {
            using var doc = JsonDocument.Parse(cut.Find("script[type='application/ld+json']").TextContent);
            root = doc.RootElement.Clone();
        });

        root.GetProperty("@type").GetString().Should().Be("Blog");
        root.GetProperty("name").GetString().Should().Be(BlogName);
        root.GetProperty("description").GetString().Should().Be(Tagline,
            "the Blog entity is site-level: described by the configured tagline on every page that emits it");
    }

    /// <summary>11 published posts, so page 2 of the 10-per-page listing carries one post.</summary>
    private static List<PostSummaryDto> ElevenPosts() =>
        [.. Enumerable.Range(1, 11).Select(n => Summary($"post-{n}", $"Post {n}"))];

    private static PostSummaryDto Summary(string slug, string title) =>
        new(Guid.CreateVersion7(), slug, title, null, null, "A summary.", 2, PostStatus.Published,
            new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc), "An Author", null, [], null, 4, 1);

    /// <summary>Renders the home page with a <see cref="HeadOutlet"/> beside it, exactly like
    /// App.razor, so the page's head output is observable in the rendered markup.</summary>
    private async Task<IRenderedComponent<ContainerFragment>> RenderHomeAsync(
        List<PostSummaryDto> posts)
    {
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient { Posts = posts });

        var cut = Render(builder =>
        {
            builder.OpenComponent<HeadOutlet>(0);
            builder.CloseComponent();
            builder.OpenComponent<Home>(1);
            builder.CloseComponent();
        });

        // The listing loads asynchronously; wait until the head output is present so callers can
        // assert synchronously (same shape as SeoHeadTests).
        await cut.WaitForAssertionAsync(() => cut.Find("link[rel='canonical']").Should().NotBeNull());
        return cut;
    }
}
