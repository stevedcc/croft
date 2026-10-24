using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Croft.Web.Components;
using Croft.Web.Components.Admin;
using Croft.Web.Components.Layout;
using Croft.Web.Services;
using Bunit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace Croft.Web.Tests.Components;

/// <summary>
/// Brief 13g item 5 (review I-6, I-7, M-1, M-3, M-4, M-5, M-6, M-9, M-12, M-13, M-14): names, roles,
/// headings and landmarks as the markup states them. The browser-level outcomes (axe over every
/// page, the accessible names a screen reader gets) are in the scan matrix and AccessibleNamesTests.
/// </summary>
public class AccessibleStructureTests : BlogBunitContext
{
    private static PostSummaryDto Summary(string title = "Notes on hills", CategoryDto? category = null) =>
        new(Guid.CreateVersion7(), "notes-on-hills", title, null, null, "A summary.", 2, PostStatus.Published,
            new DateTime(2026, 10, 2, 9, 30, 0, DateTimeKind.Utc), new DateTime(2026, 10, 2, 9, 30, 0, DateTimeKind.Utc),
            "An Author", category, [], null, 12, 3, PostKind.Post);

    [Fact]
    public void PageHeader_PutsTheMetaInAParagraphAfterTheH1_NotInsideIt()
    {
        var cut = Render<PageHeader>(p => p.Add(x => x.Title, "Tutorials").Add(x => x.Meta, "· 12 posts"));

        cut.Find("h1").TextContent.Trim().Should().Be("Tutorials", "the heading is the name alone");
        var meta = cut.Find("p.page-header__meta");
        meta.TextContent.Trim().Should().Be("· 12 posts");
        meta.PreviousElementSibling!.TagName.Should().Be("H1", "the meta follows the heading");
    }

    [Fact]
    public void PageHeader_WithoutMeta_RendersNoParagraph()
    {
        var cut = Render<PageHeader>(p => p.Add(x => x.Title, "Search"));

        cut.FindAll("p").Should().BeEmpty();
    }

    [Theory]
    [InlineData(2, "H2")]
    [InlineData(3, "H3")]
    public void PostCard_TitleLevelFollowsTheParameter(int level, string tag)
    {
        var cut = Render<PostCard>(p => p
            .Add(x => x.Post, Summary())
            .Add(x => x.HeadingLevel, level)
            .Add(x => x.Variant, PostCardVariant.List));

        cut.Find(".post-card__title").TagName.Should().Be(tag);
    }

    [Fact]
    public void PostCard_Featured_TitleLevelFollowsTheParameterToo()
    {
        var cut = Render<PostCard>(p => p
            .Add(x => x.Post, Summary())
            .Add(x => x.HeadingLevel, 2)
            .Add(x => x.Variant, PostCardVariant.Featured));

        cut.Find(".post-card__title").TagName.Should().Be("H2");
    }

    [Fact]
    public void PostCard_DefaultsToH3_BelowTheHomePagesH2()
    {
        var cut = Render<PostCard>(p => p.Add(x => x.Post, Summary()));

        cut.Find(".post-card__title").TagName.Should().Be("H3");
    }

    [Fact]
    public void PostList_PassesItsCardHeadingLevelOn()
    {
        var cut = Render<PostList>(p => p
            .Add(x => x.Items, [Summary("One"), Summary("Two")])
            .Add(x => x.CardHeadingLevel, 2));

        cut.FindAll(".post-card__title").Select(h => h.TagName).Should().OnlyContain(t => t == "H2");
    }

    [Fact]
    public void PostCard_LinkIsNamedByItsContent_NotByAnAriaLabel()
    {
        var cut = Render<PostCard>(p => p.Add(x => x.Post, Summary("Notes on hills")));

        var link = cut.Find("a.post-card__link");
        link.HasAttribute("aria-label").Should().BeFalse("an aria-label would replace the summary and date in the link's name");
        link.TextContent.Should().Contain("Notes on hills").And.Contain("A summary.");
    }

    [Theory]
    [InlineData(typeof(PostHero))]
    [InlineData(typeof(PageSurface))]
    public void PreviewComponents_RenderTheirTitleAsAnH2_WhenAskedTo(Type component)
    {
        var cut = component == typeof(PostHero)
            ? (IRenderedComponent<IComponent>)Render<PostHero>(p => p.Add(x => x.Post, FullPost()).Add(x => x.HeadingLevel, 2))
            : Render<PageSurface>(p => p.Add(x => x.Title, "A page").Add(x => x.HeadingLevel, 2).Add(x => x.Markdown, EmptyRendered()));

        cut.FindAll("h1").Should().BeEmpty("the page's own header carries the h1");
        cut.FindAll("h2").Should().ContainSingle();
    }

    [Theory]
    [InlineData(typeof(PostHero))]
    [InlineData(typeof(PageSurface))]
    public void PreviewComponents_DefaultToTheH1(Type component)
    {
        var cut = component == typeof(PostHero)
            ? (IRenderedComponent<IComponent>)Render<PostHero>(p => p.Add(x => x.Post, FullPost()))
            : Render<PageSurface>(p => p.Add(x => x.Title, "A page").Add(x => x.Markdown, EmptyRendered()));

        cut.FindAll("h1").Should().ContainSingle();
    }

    private static PostDto FullPost() =>
        new(Guid.NewGuid(), "a-post", "A post", null, "Body.", null, null, PostStatus.Published,
            new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc), "author-id", "Alice Author", null, [], null, 7);

    private static RenderedMarkdown EmptyRendered() => new(string.Empty, []);

    [Theory]
    [InlineData(12, "12 posts")]
    [InlineData(1, "1 post")]
    public void NavItem_Count_SaysWhatItCounts(int count, string expected)
    {
        var cut = Render<NavItem>(p => p
            .Add(x => x.Href, "/category/tutorials").Add(x => x.Text, "Tutorials").Add(x => x.Count, count));

        cut.Find(".count").TextContent.Trim().Should().Be(expected,
            "the link reads as 'Tutorials 12 posts', not 'Tutorials 12'");
    }

    [Fact]
    public void SidebarGroups_AreNamedByAriaLabelledby_AndNoHeadingPrecedesThePageH1()
    {
        var cut = Render<SidebarNav>(p => p
            .Add(x => x.Categories, [new CategoryDto(Guid.NewGuid(), "Programming", "programming", "Code", 3)])
            .Add(x => x.Pages, []));

        cut.FindAll("h1, h2, h3, h4, h5, h6").Should().BeEmpty("headings in the sidebar come before the page's h1 in document order");
        var group = cut.Find("[role=group][aria-labelledby]");
        var label = cut.Find($"#{group.GetAttribute("aria-labelledby")}");
        label.TextContent.Trim().Should().Be("Categories");
    }

    [Fact]
    public void MainLayout_PutsTheFooterOutsideMain_AsTheOnlyContentinfo()
    {
        var fake = new FakeBlogApiClient { FooterNavItems = [new("Impressum", "impressum")] };
        Services.AddSingleton<IBlogApiClient>(fake);
        Services.AddSingleton(new MudTheme());

        var cut = Render<MainLayout>();

        cut.FindAll("main footer").Should().BeEmpty("a contentinfo landmark inside main is nested (axe landmark-contentinfo-is-top-level)");
        var footer = cut.Find("footer[role=contentinfo]");
        footer.ParentElement!.ClassList.Should().Contain("shell", "the footer is a row of the shell grid, below main");
        footer.PreviousElementSibling!.TagName.Should().Be("MAIN");
    }

    [Fact]
    public void BlazorErrorUi_IsAnAlert_WithAKeyboardDismissButton()
    {
        var fake = new FakeBlogApiClient();
        Services.AddSingleton<IBlogApiClient>(fake);
        Services.AddSingleton(new MudTheme());

        var cut = Render<MainLayout>();

        var ui = cut.Find("#blazor-error-ui");
        ui.GetAttribute("role").Should().Be("alert");
        var dismiss = ui.QuerySelector("button.dismiss")!;
        dismiss.GetAttribute("type").Should().Be("button");
        dismiss.GetAttribute("aria-label").Should().Be("Dismiss");
        ui.QuerySelectorAll("span.dismiss").Should().BeEmpty("a span is neither focusable nor named");
    }

    [Fact]
    public void EditorToolbar_IsAPlainGroup_NotAToolbarWithoutRovingTabindex()
    {
        var cut = Render<MarkdownEditor>(p => p.Add(x => x.Value, string.Empty));

        cut.FindAll("[role=toolbar]").Should().BeEmpty();
        var group = cut.Find(".md-editor__toolbar");
        group.GetAttribute("role").Should().Be("group");
        group.GetAttribute("aria-label").Should().Be("Formatting");
        cut.FindAll(".md-editor__toolbar button").Should().OnlyContain(b => !b.HasAttribute("tabindex"), "every button is a tab stop");
    }

    [Fact]
    public void BannerPicker_TitleIsAnH2_NotAnH3UnderTheH1()
    {
        var cut = Render<BannerPicker>();

        cut.Find(".banner-picker__title").TagName.Should().Be("H2");
    }

    [Fact]
    public void ContentsToggle_NameStartsWithContents_AndContainsWhatTheScriptWritesIntoIt()
    {
        // Brief 15d item 1 (axe 4.14 label-content-name-mismatch): post.js writes the current
        // section into the row; the accessible name must contain that visible text.
        var headings = new[] { new Heading(2, "One", "one"), new Heading(2, "Two", "two") };
        var cut = Render<ContentsPanel>(p => p.Add(x => x.Headings, headings));
        var row = cut.Find("button.toc-inline__row");
        row.QuerySelector(".toc-inline__row-text")!.TextContent = "Section 4";

        var text = string.Join(' ', row.TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var ariaLabel = row.GetAttribute("aria-label");
        var name = ariaLabel ?? text;

        if (ariaLabel is not null)
        {
            ariaLabel.Should().Contain(text, "the button's text content must be part of its aria-label");
        }

        name.Should().StartWith("Contents").And.Contain("Section 4");
    }

    [Fact]
    public void NotFoundContent_HasTheH1_InBothRenderings()
    {
        var staticDocument = Render<NotFoundContent>(p => p.AddCascadingValue("InteractiveDocument", false));
        staticDocument.Find("h1").TextContent.Should().Be("This page has wandered off");
        staticDocument.FindAll("h3").Should().BeEmpty();

        var interactive = Render<NotFoundContent>();
        interactive.Find("h1").TextContent.Should().Be("This page has wandered off");
        interactive.FindAll("h3").Should().BeEmpty();
    }

    [Fact]
    public void ErrorPage_HasTheH1_AndNoStockTemplateCopy()
    {
        var cut = Render<Croft.Web.Components.Pages.Error>();

        cut.Find("h1").TextContent.Should().Be("Something went wrong");
        cut.Markup.Should().NotContain("Development Mode").And.NotContain("ASPNETCORE_ENVIRONMENT")
            .And.NotContain("Swapping to");
        cut.Markup.Should().Contain("An error occurred while processing your request.");
    }

    [Fact]
    public void PostsGrid_NamesTheCategory_AndEachRowButtonWithThePostTitle()
    {
        var hiking = new CategoryDto(Guid.CreateVersion7(), "Hiking", "hiking", "Terrain", 4);
        var post = Summary("Notes on hills", hiking);
        var cut = Render<PostsGrid>(p => p.Add(x => x.ServerData, (_, _) =>
            Task.FromResult(new GridData<PostSummaryDto> { Items = [post], TotalItems = 1 })));

        var row = cut.Find(".posts-grid__row");
        row.QuerySelector(".posts-grid__category")!.TextContent.Trim().Should().Be("Hiking",
            "the category cell is not an unnamed icon");
        string Label(string text) =>
            row.QuerySelectorAll("a, button").Single(e => e.TextContent == text).GetAttribute("aria-label")!;
        Label("Edit").Should().Be("Edit Notes on hills");
        Label("View").Should().Be("View Notes on hills");
        Label("Analytics").Should().Be("Analytics for Notes on hills");
        Label("Feedback").Should().Be("Feedback for Notes on hills");
        Label("Delete").Should().Be("Delete Notes on hills");
    }

    [Fact]
    public void PostsGrid_ReadOnlyReviewLink_IsNamedWithThePostTitleToo()
    {
        var post = Summary("A draft") with { Status = PostStatus.Draft, PublishedAt = null };
        var cut = Render<PostsGrid>(p => p
            .Add(x => x.ReadOnly, true)
            .Add(x => x.ServerData, (_, _) => Task.FromResult(new GridData<PostSummaryDto> { Items = [post], TotalItems = 1 })));

        cut.Find("a.posts-grid__review").GetAttribute("aria-label").Should().Be("Review A draft");
    }

    [Fact]
    public void MediaPicker_TileWithoutAltText_DoesNotRepeatTheFileNameAsAlt()
    {
        var id = Guid.CreateVersion7();
        var asset = new MediaAssetDto(id, $"{id:N}", "photo.png", "image/png", $"/api/media/{id}", MediaAssetScope.Global, null, null);
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient { GlobalMedia = [asset] });

        var cut = Render<MediaGrid>(p => p.Add(g => g.Mode, MediaGridMode.Pick));

        var pick = cut.Find(".media-tile__pick");
        pick.QuerySelector("img")!.GetAttribute("alt").Should().Be(string.Empty,
            "the visible file name already names the tile; the same words as alt text are read twice");
        pick.QuerySelector(".media-tile__name")!.TextContent.Should().Be("photo.png");
    }
}
