using Croft.Web.Components.Admin;
using Bunit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Croft.Web.Tests.Components;

/// <summary>
/// IconPicker (spec §6.5/§7.4): search over <see cref="Croft.Web.Services.IIconCatalog"/>, the
/// virtualised icon grid, and the "Default (folder)" option that clears the icon. The fake
/// catalogue holds Folder, Landscape and Terrain.
/// </summary>
public class IconPickerTests : BlogBunitContext
{
    private static IReadOnlyList<string> GridTileNames(IRenderedComponent<IconPicker> cut) =>
        [.. cut.FindAll(".icon-picker__row .icon-picker__name").Select(e => e.TextContent)];

    [Fact]
    public void IconPicker_Search_Filters()
    {
        var cut = Render<IconPicker>();

        GridTileNames(cut).Should().Equal(
            ["Folder", "Landscape", "Terrain"],
            "the grid starts with every icon in the catalogue");

        cut.Find("input").Input("terr");

        GridTileNames(cut).Should().Equal(
            ["Terrain"],
            "the search must filter the grid to the matching icon names");
    }

    [Fact]
    public void IconPicker_SelectDefault_SetsNull()
    {
        var received = new List<string?>();
        var cut = Render<IconPicker>(p => p
            .Add(x => x.Value, "Terrain")
            .Add(x => x.ValueChanged, EventCallback.Factory.Create<string?>(this, received.Add)));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Default (folder)")).Click(new MouseEventArgs());

        received.Should().ContainSingle().Which.Should().BeNull(
            "the Default (folder) option sets the icon name to null");
    }
}
