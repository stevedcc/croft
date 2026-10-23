namespace Croft.Web;

/// <summary>
/// The palette tokens a site's <c>branding.json</c> may set, exactly these 18, and the text pairs
/// whose contrast is checked at startup. Token names are the CSS custom properties theme.css
/// generates (<c>--bg</c> and friends).
/// </summary>
public static class PaletteTokens
{
    public const string Bg = "bg";
    public const string Surface = "surface";
    public const string Text = "text";
    public const string Muted = "muted";
    public const string Sidebar = "sidebar";
    public const string SidebarText = "sidebar-text";
    public const string Primary = "primary";
    public const string PrimaryStrong = "primary-strong";
    public const string PrimaryHover = "primary-hover";
    public const string PrimaryContainer = "primary-container";
    public const string OnPrimary = "on-primary";
    public const string Secondary = "secondary";
    public const string SecondaryContainer = "secondary-container";
    public const string OnSecondaryContainer = "on-secondary-container";
    public const string OnSecondary = "on-secondary";
    public const string CodeBg = "code-bg";
    public const string CodeText = "code-text";
    public const string FallbackWash = "fallback-wash";

    /// <summary>Every token, in the order theme.css writes them.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Bg, Surface, Text, Muted, Sidebar, SidebarText,
        Primary, PrimaryStrong, PrimaryHover, PrimaryContainer, OnPrimary,
        Secondary, SecondaryContainer, OnSecondaryContainer, OnSecondary,
        CodeBg, CodeText, FallbackWash,
    ];

    /// <summary>A foreground that is not a palette token: the literal white (#FFFFFF) the stylesheets put on
    /// <see cref="PrimaryStrong"/> fills (the current navigation item, the current pager link, the skip
    /// link and the hover of the editor's buttons). It stands in the pair lists so that the fill is checked
    /// against white; <see cref="ValueOf"/> resolves it.</summary>
    public const string FixedWhite = "#FFFFFF";

    /// <summary>The value of a token in a palette, or white for <see cref="FixedWhite"/>.</summary>
    public static string ValueOf(BrandPalette palette, string token) =>
        token == FixedWhite ? FixedWhite : palette[token];

    /// <summary>
    /// The foreground/background pairs that must reach WCAG AA (4.5:1) in both modes: every pair the
    /// stylesheets put together (PaletteGuardTests reads the CSS and fails when a pair is missing here).
    /// Pairs whose background comes from an ancestor rather than the same rule are listed by hand from
    /// where the CSS sets each foreground.
    /// </summary>
    public static readonly IReadOnlyList<(string Foreground, string Background)> ContrastPairs =
    [
        (Text, Bg),
        (Text, Surface),
        (Text, PrimaryContainer),
        (Muted, Bg),
        (Muted, Surface),
        (Muted, Sidebar),
        (Primary, Bg),
        (Primary, Surface),
        (Primary, PrimaryContainer),
        (OnPrimary, Primary),
        (FixedWhite, PrimaryStrong),
        (OnSecondaryContainer, SecondaryContainer),
        (OnSecondaryContainer, Bg),
        (SidebarText, Sidebar),
        (CodeText, CodeBg),
    ];

    /// <summary>The pairs whose boundary is not text — field borders and focus rings — and must reach 3:1
    /// (WCAG 1.4.11) in both modes.</summary>
    public static readonly IReadOnlyList<(string Foreground, string Background)> NonTextContrastPairs =
    [
        (Muted, Bg),
        (Muted, Surface),
        (Primary, Bg),
        (Primary, Surface),
    ];
}
