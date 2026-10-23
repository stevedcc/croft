using Croft.Shared.Contracts;
using Croft.Web.Components;
using Croft.Web.Components.Admin;
using Bunit;
using AwesomeAssertions;

namespace Croft.Web.Tests.Components;

/// <summary>
/// Task R3.2: the green category chip (spec §7.3) — category icon + name, linking to the category
/// page. Review Focus 1: no category means no chip at all, never an empty one.
/// </summary>
public class CategoryChipTests : BlogBunitContext
{
    [Fact]
    public void CategoryChip_WithCategory_LinksToCategoryPage()
    {
        var category = new CategoryDto(Guid.NewGuid(), "Programming", "programming", "Code", 5);

        var cut = Render<CategoryChip>(p => p.Add(x => x.Category, category));

        var link = cut.Find("a.category-chip");
        link.GetAttribute("href").Should().Be("/category/programming");
        link.TextContent.Should().Contain("Programming");
        cut.FindComponent<CategoryIcon>().Instance.Name.Should().Be("Code");
    }

    [Fact]
    public void CategoryChip_LongName_EllipsizesWithFullTitle()
    {
        // Review Focus 2: a 60-char category name must not overflow the card.
        var name = new string('x', 60);
        var category = new CategoryDto(Guid.NewGuid(), name, "long", null, 1);

        var cut = Render<CategoryChip>(p => p.Add(x => x.Category, category));

        var link = cut.Find("a.category-chip");
        link.GetAttribute("title").Should().Be(name);
        link.QuerySelector(".category-chip__name")!.TextContent.Should().Be(name);
    }

    [Fact]
    public void CategoryChip_NoCategory_RendersNothing()
    {
        // Review Focus 1: no empty chip.
        var cut = Render<CategoryChip>(p => p.Add(x => x.Category, null));

        cut.Markup.Should().BeEmpty();
    }
}
