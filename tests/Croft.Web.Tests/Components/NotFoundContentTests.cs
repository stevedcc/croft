using Croft.Web.Components;
using Bunit;
using AwesomeAssertions;

namespace Croft.Web.Tests.Components;

/// <summary>
/// Task R3.5: NotFoundContent — the not-found copy (spec §5.5) plus the Home and Search links.
/// Ruling R3-R0-a: the two links must stay text-variant primary MudButtons — they host ThemeTests'
/// dark-mode contrast probe (which locates exactly two
/// <c>main a.mud-button-root.mud-button-text-primary</c> elements).
/// </summary>
public class NotFoundContentTests : BlogBunitContext
{
    [Fact]
    public void NotFoundContent_ShowsCopy_AndTheTwoProbeLinks()
    {
        var cut = Render<NotFoundContent>();

        cut.Markup.Should().Contain("This page has wandered off", "spec §5.5 fixes the not-found copy.");

        var links = cut.FindAll("a.mud-button-root.mud-button-text-primary");
        links.Select(l => l.GetAttribute("href")).Should().Equal(["/", "/search"],
            "spec §5.5 requires links to Home and Search");
        links.Select(l => l.TextContent.Trim()).Should().Equal(["Home", "Search"]);
    }

    [Fact]
    public void NotFoundContent_InAStaticDocument_IsPlainMarkup_WithTheSameCopyAndLinks()
    {
        // A missing post, category, tag or page: the reading page calls NotFound() and this
        // content renders inside its own static document, which loads no MudBlazor.
        var cut = Render<NotFoundContent>(p => p.AddCascadingValue("InteractiveDocument", false));

        cut.Markup.Should().NotContain("mud-", "the static document has no MudBlazor stylesheet to give those classes meaning");
        cut.Find("h1").TextContent.Should().Be("This page has wandered off"); // was h3: brief 13g item 5 (review M-6)
        var links = cut.FindAll("a.not-found__link");
        links.Select(l => l.GetAttribute("href")).Should().Equal(["/", "/search"]);
        links.Select(l => l.TextContent.Trim()).Should().Equal(["Home", "Search"]);
        cut.FindAll("[style]").Should().BeEmpty("the public policy bans style attributes");
    }
}
