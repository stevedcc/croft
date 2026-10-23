using Croft.Web.Components;
using Croft.Web.Services;
using Bunit;
using AwesomeAssertions;

namespace Croft.Web.Tests.Components;

/// <summary>
/// Brief 07g item 3's shared page surface (Components/Blog/PageSurface): the plain title header
/// (title, optional subtitle, the date line) and the Markdown body in the reading column — no
/// hero image, no rail, no Related, no reading time, no feedback. The date line follows the
/// item's date rule: the published date, plus "· Updated {date}" when the content was edited on
/// a later UTC calendar day. The admin editor's page preview reuses this component unchanged.
/// </summary>
public class PageSurfaceTests : BlogBunitContext
{
    private static readonly DateTime Published = new(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SameDayEdit = new(2026, 10, 2, 18, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime LaterDayEdit = new(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc);

    private static readonly RenderedMarkdown Body = new("<p>The hillsideword waits.</p>", []);

    private IRenderedComponent<PageSurface> RenderSurface(
        DateTime? publishedAt = null, DateTime? contentEditedAt = null, string? subtitle = null) =>
        Render<PageSurface>(p => p
            .Add(x => x.Title, "About")
            .Add(x => x.Subtitle, subtitle)
            .Add(x => x.PublishedAt, publishedAt ?? Published)
            .Add(x => x.ContentEditedAt, contentEditedAt)
            .Add(x => x.Markdown, Body));

    [Fact]
    public void PageSurface_RendersTheTitleHeader_DateLine_AndBody()
    {
        var cut = RenderSurface(subtitle: "Notes on the owner");

        cut.Find("h1").TextContent.Should().Be("About");
        cut.Find(".page-surface__subtitle").TextContent.Should().Be("Notes on the owner");
        cut.Find(".post-meta__when").TextContent.Should().Be("Published on 2 October 2026",
            "the date line is the published date — a page shows no reading time");
        cut.Find(".markdown-body").InnerHtml.Should().Contain("The hillsideword waits.");
    }

    [Fact]
    public void PageSurface_Subtitle_IsOptional()
    {
        var cut = RenderSurface();

        cut.FindAll(".page-surface__subtitle").Should().BeEmpty("no subtitle, no empty element");
    }

    [Fact]
    public void PageSurface_EditedOnALaterUtcDay_ShowsTheUpdatedDate()
    {
        var cut = RenderSurface(contentEditedAt: LaterDayEdit);

        cut.Find(".post-meta__when").TextContent.Should().Be("Published on 2 October 2026, last updated on 5 October 2026");
    }

    [Fact]
    public void PageSurface_EditedTheSameUtcDay_ShowsNoUpdatedDate()
    {
        var cut = RenderSurface(contentEditedAt: SameDayEdit);

        cut.Find(".post-meta__when").TextContent.Should().Be("Published on 2 October 2026");
    }

    [Fact]
    public void PageSurface_CarriesNoneOfThePostSurfaces()
    {
        var cut = RenderSurface(subtitle: "Notes on the owner");

        cut.FindAll(".post-hero").Should().BeEmpty("no hero image");
        cut.FindAll(".post-rail").Should().BeEmpty("no rail");
        cut.FindAll(".related-posts").Should().BeEmpty("no Related section");
        cut.Markup.Should().NotContain("min read", "no reading time");
    }
}
