using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Croft.Web.Services;
using Croft.Web.Components.Pages;
using Bunit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests.Components;

/// <summary>
/// Brief 10b item 3: the home page's site masthead. On page 1 a slim strip above "Latest Posts"
/// carries the site's name (the page's h1) and description (the tagline when none); "Latest
/// Posts" becomes an h2. Page 2 and later, and a site with <c>Masthead</c> off, render the plain
/// list-page header as before (h1 "Latest Posts"). The masthead is plain markup: no MudBlazor, no
/// script and no style attribute (the image arrives through theme.css's tokens).
/// </summary>
public class HomeMastheadTests : BlogBunitContext
{
    private static readonly Croft.Web.Branding Described = new()
    {
        Name = "Field Notes",
        Tagline = "A tagline",
        Description = "Notes from the field.",
    };

    [Fact]
    public async Task Page1_ShowsTheMastheadAboveTheListHeader_NameIsTheH1_LatestPostsTheH2()
    {
        var cut = await RenderHomeAsync(Described);

        var masthead = cut.Find(".masthead");
        masthead.QuerySelector("h1")!.TextContent.Trim().Should().Be("Field Notes");
        masthead.TextContent.Should().Contain("Notes from the field.");
        cut.FindAll("h1").Should().ContainSingle("the home page has one h1: the site name");
        cut.Find("h2.page-header__title").TextContent.Should().Contain("Latest Posts");
        cut.Markup.IndexOf("masthead", StringComparison.Ordinal).Should()
            .BeLessThan(cut.Markup.IndexOf("Latest Posts", StringComparison.Ordinal), "the masthead sits above the list header");
    }

    [Fact]
    public async Task Page1_WithoutADescription_ShowsTheTagline()
    {
        var cut = await RenderHomeAsync(Described with { Description = string.Empty });

        cut.Find(".masthead").TextContent.Should().Contain("A tagline");
    }

    [Fact]
    public async Task Masthead_IsPlainMarkup_NoStyleAttributeScriptOrMudBlazor()
    {
        var cut = await RenderHomeAsync(Described);

        var masthead = cut.Find(".masthead");
        masthead.OuterHtml.Should().NotContain("style=", "the public CSP bans style attributes; the image is a theme token");
        masthead.QuerySelectorAll("script").Should().BeEmpty();
        masthead.OuterHtml.Should().NotContain("mud-");
    }

    [Fact]
    public async Task Page2_HasNoMasthead_AndLatestPostsIsTheH1()
    {
        var cut = await RenderHomeAsync(Described);

        Services.GetRequiredService<NavigationManager>().NavigateTo("/?page=2");
        cut.Instance.PageNumber = "2";
        cut.Render();

        await cut.WaitForAssertionAsync(() =>
        {
            cut.Markup.Should().Contain("Post 11", "page 2 has loaded");
            cut.FindAll(".masthead").Should().BeEmpty("the masthead is page 1 only");
            cut.Find("h1.page-header__title").TextContent.Should().Contain("Latest Posts");
            cut.FindAll("h1").Should().ContainSingle();
        });
    }

    [Fact]
    public async Task MastheadSwitchedOff_RendersNoMasthead_AndLatestPostsIsTheH1()
    {
        var cut = await RenderHomeAsync(Described with { Masthead = false });

        cut.FindAll(".masthead").Should().BeEmpty();
        cut.Find("h1.page-header__title").TextContent.Should().Contain("Latest Posts");
        cut.FindAll("h1").Should().ContainSingle();
    }

    [Fact]
    public async Task Home_AsksForThePublicView_WhoeverIsSignedIn()
    {
        Services.AddSingleton(Described);
        var fake = new FakeBlogApiClient { Posts = ElevenPosts() };
        Services.AddSingleton<IBlogApiClient>(fake);

        var cut = Render<Home>();
        await cut.WaitForAssertionAsync(() => cut.FindAll(".post-card").Should().NotBeEmpty());

        fake.PostRequests.Should().NotBeEmpty().And.OnlyContain(r => r.PublicView,
            "an admin's home page lists what visitors see: no pages, drafts or scheduled posts");
    }

    private async Task<IRenderedComponent<Home>> RenderHomeAsync(Croft.Web.Branding branding)
    {
        Services.AddSingleton(branding);
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient { Posts = ElevenPosts() });

        var cut = Render<Home>();
        await cut.WaitForAssertionAsync(() => cut.FindAll(".post-card").Should().NotBeEmpty());
        return cut;
    }

    private static List<PostSummaryDto> ElevenPosts() =>
        [.. Enumerable.Range(1, 11).Select(n =>
            new PostSummaryDto(Guid.CreateVersion7(), $"post-{n}", $"Post {n}", null, null, "A summary.", 2,
                PostStatus.Published, new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc), "An Author", null, [], null, 4, 1))];
}
