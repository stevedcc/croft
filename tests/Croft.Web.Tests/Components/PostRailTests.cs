using Croft.Shared.Contracts;
using Croft.Web.Components;
using Croft.Web.Services;
using Bunit;
using AwesomeAssertions;

namespace Croft.Web.Tests.Components;

/// <summary>
/// Task R3.4: the 220px right-hand column (spec §5.4) — On this page, Tags and the caller's extra
/// panels (ChildContent); Related left the rail for a section after the body (brief 07d item 3,
/// so its input and expectation are gone from here — disclosed in the item's commit). Review
/// Focus 1: sections with no content render no heading and no empty panel. Ruling R3-4-a: the
/// rail forwards Path so its TOC links are path-qualified.
/// </summary>
public class PostRailTests : BlogBunitContext
{
    private static readonly TagDto BlazorTag = new(Guid.NewGuid(), "blazor", "blazor", 0, true);

    [Fact]
    public void PostRail_NothingToFill_RendersNoPanels()
    {
        var cut = Render<PostRail>();

        cut.Markup.Should().BeEmpty("empty sections render nothing — no empty headings (Review Focus 1)");
    }

    [Fact]
    public void PostRail_ShowsTocTagsAndChildContent()
    {
        var cut = Render<PostRail>(p => p
            .Add(x => x.Headings,
            [
                new Heading(2, "Introduction", "introduction"),
                new Heading(2, "Deep dive", "deep-dive"),
            ])
            .Add(x => x.Tags, [BlazorTag])
            .Add(x => x.Path, "/post/demo")
            .AddChildContent("<div class=\"extra-panel\">Feedback</div>"));

        cut.Find(".toc");
        cut.Find(".tag-pills").TextContent.Should().Contain("#blazor");
        cut.Find(".extra-panel");
        cut.Markup.Should().Contain("/post/demo#introduction",
            "the rail's TOC links are path-qualified (ruling R3-4-a)");
    }
}
