using Croft.Web.Services;
using AwesomeAssertions;
using MudBlazor;

namespace Croft.Web.Tests;

public class IconCatalogTests
{
    private readonly IconCatalog _catalog = new();

    [Fact]
    public void Resolve_Unknown_ReturnsFolder()
    {
        _catalog.Resolve("NoSuchIcon").Should().Be(Icons.Material.Filled.Folder);
    }

    [Fact]
    public void Resolve_Null_ReturnsFolder()
    {
        _catalog.Resolve(null).Should().Be(Icons.Material.Filled.Folder);
    }

    [Fact]
    public void Resolve_Terrain_ReturnsTerrainPath()
    {
        var path = _catalog.Resolve("Terrain");

        // The actual SVG path content of the Terrain icon, not just any string back.
        path.Should().Be(Icons.Material.Filled.Terrain).And.Contain("<path");
    }

    [Fact]
    public void Search_Mount_IncludesTerrainOrLandscape_PrefixFirst()
    {
        // Spec §6.5: the icon picker matches "mount" on name (→ Terrain). No Material icon
        // name contains "mount", so the curated synonym map supplies the matches (ruling R2-R5).
        var results = _catalog.Search("mount");

        results.Should().Contain("Terrain").And.Contain("Landscape");

        // "mount" has no prefix matches, so the prefix-first rule (ruling R2-R5) shows up on a
        // query that has both kinds: every "land…" name must come before the substring-only ones.
        var land = _catalog.Search("land").ToList();
        var prefixMatches = land.Count(n => n.StartsWith("land", StringComparison.OrdinalIgnoreCase));

        prefixMatches.Should().BeGreaterThan(0);
        land.Take(prefixMatches).Should()
            .OnlyContain(n => n.StartsWith("land", StringComparison.OrdinalIgnoreCase));
        land.Skip(prefixMatches).Should().NotBeEmpty().And
            .OnlyContain(n => !n.StartsWith("land", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Names_HasHundreds()
    {
        // The interface contract: every constant (> 1000 in MudBlazor 9.11.0), sorted.
        _catalog.Names.Count.Should().BeGreaterThan(1000);
        _catalog.Names.Should().BeInAscendingOrder(StringComparer.Ordinal);
    }
}
