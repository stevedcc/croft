using Croft.Web.Theme;
using AwesomeAssertions;
using MudBlazor;
using MudBlazor.Utilities;

namespace Croft.Web.Tests;

/// <summary>
/// BlogTheme is built from the site's Branding (brief 07a item 2), so MudBlazor-styled controls
/// and the app's own markup share one palette: Background=bg, Surface=surface,
/// BackgroundGray=sidebar, TextPrimary=text, TextSecondary=muted, Drawer/Appbar Background=sidebar,
/// Drawer/Appbar Text and DrawerIcon=sidebar-text, Primary=primary, PrimaryContrastText=on-primary,
/// PrimaryDarken=primary-hover, PrimaryLighten=primary-container, Secondary=secondary,
/// SecondaryContrastText=on-secondary, SecondaryLighten=secondary-container — for each mode.
/// Values are compared as colours (MudBlazor normalises "#090000" to "rgb(9,0,0)").
/// </summary>
public class BlogThemeTests
{
    /// <summary>A palette where every token's value is a unique colour across tokens and modes.</summary>
    private static Branding KnownBranding()
    {
        Dictionary<string, string> light = [];
        Dictionary<string, string> dark = [];
        for (var i = 0; i < PaletteTokens.All.Count; i++)
        {
            light[PaletteTokens.All[i]] = $"#{i + 1:X2}0000";
            dark[PaletteTokens.All[i]] = $"#0000{i + 1:X2}";
        }

        return Branding.Defaults with
        {
            Light = new(light),
            Dark = new(dark),
        };
    }

    private static MudColor Colour(MudColor value) => value;

    private static MudColor Colour(string value) => new(value);

    [Fact]
    public void Create_LightPalette_MapsEveryFieldToItsToken()
    {
        var branding = KnownBranding();
        var palette = BlogTheme.Create(branding).PaletteLight;
        var tokens = branding.Light;

        Colour(palette.Background).Should().Be(new MudColor(tokens["bg"]));
        Colour(palette.Surface).Should().Be(new MudColor(tokens["surface"]));
        Colour(palette.BackgroundGray).Should().Be(new MudColor(tokens["sidebar"]));
        Colour(palette.TextPrimary).Should().Be(new MudColor(tokens["text"]));
        Colour(palette.TextSecondary).Should().Be(new MudColor(tokens["muted"]));
        Colour(palette.DrawerBackground).Should().Be(new MudColor(tokens["sidebar"]));
        Colour(palette.DrawerText).Should().Be(new MudColor(tokens["sidebar-text"]));
        Colour(palette.DrawerIcon).Should().Be(new MudColor(tokens["sidebar-text"]));
        Colour(palette.AppbarBackground).Should().Be(new MudColor(tokens["sidebar"]));
        Colour(palette.AppbarText).Should().Be(new MudColor(tokens["sidebar-text"]));
        Colour(palette.Primary).Should().Be(new MudColor(tokens["primary"]));
        Colour(palette.PrimaryContrastText).Should().Be(new MudColor(tokens["on-primary"]));
        Colour(palette.PrimaryDarken).Should().Be(new MudColor(tokens["primary-hover"]));
        Colour(palette.PrimaryLighten).Should().Be(new MudColor(tokens["primary-container"]));
        Colour(palette.Secondary).Should().Be(new MudColor(tokens["secondary"]));
        Colour(palette.SecondaryContrastText).Should().Be(new MudColor(tokens["on-secondary"]));
        Colour(palette.SecondaryLighten).Should().Be(new MudColor(tokens["secondary-container"]));
    }

    [Fact]
    public void Create_DarkPalette_MapsEveryFieldToItsToken()
    {
        var branding = KnownBranding();
        var palette = BlogTheme.Create(branding).PaletteDark;
        var tokens = branding.Dark;

        Colour(palette.Background).Should().Be(new MudColor(tokens["bg"]));
        Colour(palette.Surface).Should().Be(new MudColor(tokens["surface"]));
        Colour(palette.BackgroundGray).Should().Be(new MudColor(tokens["sidebar"]));
        Colour(palette.TextPrimary).Should().Be(new MudColor(tokens["text"]));
        Colour(palette.TextSecondary).Should().Be(new MudColor(tokens["muted"]));
        Colour(palette.DrawerBackground).Should().Be(new MudColor(tokens["sidebar"]));
        Colour(palette.DrawerText).Should().Be(new MudColor(tokens["sidebar-text"]));
        Colour(palette.DrawerIcon).Should().Be(new MudColor(tokens["sidebar-text"]));
        Colour(palette.AppbarBackground).Should().Be(new MudColor(tokens["sidebar"]));
        Colour(palette.AppbarText).Should().Be(new MudColor(tokens["sidebar-text"]));
        Colour(palette.Primary).Should().Be(new MudColor(tokens["primary"]));
        Colour(palette.PrimaryContrastText).Should().Be(new MudColor(tokens["on-primary"]));
        Colour(palette.PrimaryDarken).Should().Be(new MudColor(tokens["primary-hover"]));
        Colour(palette.PrimaryLighten).Should().Be(new MudColor(tokens["primary-container"]));
        Colour(palette.Secondary).Should().Be(new MudColor(tokens["secondary"]));
        Colour(palette.SecondaryContrastText).Should().Be(new MudColor(tokens["on-secondary"]));
        Colour(palette.SecondaryLighten).Should().Be(new MudColor(tokens["secondary-container"]));
    }

    [Fact]
    public void Create_DarkPalette_IsAPaletteDark()
    {
        var theme = BlogTheme.Create(KnownBranding());

        theme.PaletteDark.Should().BeOfType<PaletteDark>();
    }

    /// <summary>
    /// Fields the branding does not map keep MudBlazor's own dark defaults (a light palette
    /// here would give light-grey table lines and near-invisible actions in dark mode).
    /// </summary>
    [Fact]
    public void Create_DarkPalette_KeepsMudBlazorDarkDefaultsForUnmappedFields()
    {
        var palette = BlogTheme.Create(KnownBranding()).PaletteDark;
        var defaults = new PaletteDark();

        palette.TableLines.Should().Be(defaults.TableLines);
        palette.LinesDefault.Should().Be(defaults.LinesDefault);
        palette.LinesInputs.Should().Be(defaults.LinesInputs);
        palette.Divider.Should().Be(defaults.Divider);
        palette.ActionDefault.Should().Be(defaults.ActionDefault);
        palette.ActionDisabled.Should().Be(defaults.ActionDisabled);
        palette.TextDisabled.Should().Be(defaults.TextDisabled);
        palette.OverlayDark.Should().Be(defaults.OverlayDark);
        palette.Skeleton.Should().Be(defaults.Skeleton);
    }
}
