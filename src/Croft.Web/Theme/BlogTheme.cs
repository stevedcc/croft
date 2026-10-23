using MudBlazor;

namespace Croft.Web.Theme;

/// <summary>
/// The MudBlazor theme built from the site's Branding, so MudBlazor-styled controls and the
/// app's own markup share one palette (the same values theme.css writes as custom properties):
/// Background=bg, Surface=surface, BackgroundGray=sidebar, TextPrimary=text, TextSecondary=muted,
/// Drawer/Appbar Background=sidebar, Drawer/Appbar Text and DrawerIcon=sidebar-text,
/// Primary=primary, PrimaryContrastText=on-primary, PrimaryDarken=primary-hover,
/// PrimaryLighten=primary-container, Secondary=secondary, SecondaryContrastText=on-secondary,
/// SecondaryLighten=secondary-container — for each mode. Each mode builds its own palette type
/// (PaletteLight / PaletteDark), so fields the branding does not map keep MudBlazor's per-mode
/// defaults. Program.cs feeds this to MudThemeProvider together with the effective IsDarkMode.
/// </summary>
public static class BlogTheme
{
    public static MudTheme Create(Branding branding) => new()
    {
        PaletteLight = Palette<PaletteLight>(branding.Light),
        PaletteDark = Palette<PaletteDark>(branding.Dark),
    };

    private static T Palette<T>(BrandPalette tokens) where T : Palette, new() => new()
    {
        Background = tokens[PaletteTokens.Bg],
        Surface = tokens[PaletteTokens.Surface],
        BackgroundGray = tokens[PaletteTokens.Sidebar],
        TextPrimary = tokens[PaletteTokens.Text],
        TextSecondary = tokens[PaletteTokens.Muted],
        DrawerBackground = tokens[PaletteTokens.Sidebar],
        DrawerText = tokens[PaletteTokens.SidebarText],
        DrawerIcon = tokens[PaletteTokens.SidebarText],
        AppbarBackground = tokens[PaletteTokens.Sidebar],
        AppbarText = tokens[PaletteTokens.SidebarText],
        Primary = tokens[PaletteTokens.Primary],
        PrimaryContrastText = tokens[PaletteTokens.OnPrimary],
        PrimaryDarken = tokens[PaletteTokens.PrimaryHover],
        PrimaryLighten = tokens[PaletteTokens.PrimaryContainer],
        Secondary = tokens[PaletteTokens.Secondary],
        SecondaryContrastText = tokens[PaletteTokens.OnSecondary],
        SecondaryLighten = tokens[PaletteTokens.SecondaryContainer],
    };
}
