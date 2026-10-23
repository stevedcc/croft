using System.Collections.Frozen;

namespace Croft.Web;

/// <summary>
/// One mode's (light or dark) 18 colour tokens. Immutable: a site's <c>branding.json</c> is merged
/// over <see cref="DefaultLight"/>/<see cref="DefaultDark"/> token by token, producing a new
/// palette. Values are kept exactly as written (all of <c>#RGB</c>, <c>#RRGGBB</c>,
/// <c>#RRGGBBAA</c>, <c>rgb()</c> and <c>rgba()</c> are valid CSS).
/// </summary>
public sealed record BrandPalette(IReadOnlyDictionary<string, string> Tokens)
{
    public string this[string token] => Tokens[token];

    public static BrandPalette DefaultLight { get; } = new(new Dictionary<string, string>
    {
        [PaletteTokens.Bg] = "#F8FAFC",
        [PaletteTokens.Surface] = "#FFFFFF",
        [PaletteTokens.Text] = "#0F172A",
        [PaletteTokens.Muted] = "#475569",
        [PaletteTokens.Sidebar] = "#F1F5F9",
        [PaletteTokens.SidebarText] = "#1E293B",
        [PaletteTokens.Primary] = "#1D4ED8",
        [PaletteTokens.PrimaryStrong] = "#1D4ED8",
        [PaletteTokens.PrimaryHover] = "#1E40AF",
        [PaletteTokens.PrimaryContainer] = "#DBEAFE",
        [PaletteTokens.OnPrimary] = "#FFFFFF",
        [PaletteTokens.Secondary] = "#0F766E",
        [PaletteTokens.SecondaryContainer] = "#CCFBF1",
        [PaletteTokens.OnSecondaryContainer] = "#134E4A",
        [PaletteTokens.OnSecondary] = "#FFFFFF",
        [PaletteTokens.CodeBg] = "#1E293B",
        [PaletteTokens.CodeText] = "#E2E8F0",
        [PaletteTokens.FallbackWash] = "rgba(15,23,42,.38)",
    }.ToFrozenDictionary());

    public static BrandPalette DefaultDark { get; } = new(new Dictionary<string, string>
    {
        [PaletteTokens.Bg] = "#0F172A",
        [PaletteTokens.Surface] = "#1E293B",
        [PaletteTokens.Text] = "#E2E8F0",
        [PaletteTokens.Muted] = "#94A3B8",
        [PaletteTokens.Sidebar] = "#131C2E",
        [PaletteTokens.SidebarText] = "#CBD5E1",
        [PaletteTokens.Primary] = "#93C5FD",
        [PaletteTokens.PrimaryStrong] = "#1E3A8A",
        [PaletteTokens.PrimaryHover] = "#1E40AF",
        [PaletteTokens.PrimaryContainer] = "#1E2A4A",
        [PaletteTokens.OnPrimary] = "#0F172A",
        [PaletteTokens.Secondary] = "#5EEAD4",
        [PaletteTokens.SecondaryContainer] = "#134E4A",
        [PaletteTokens.OnSecondaryContainer] = "#99F6E4",
        [PaletteTokens.OnSecondary] = "#0F172A",
        [PaletteTokens.CodeBg] = "#0B1220",
        [PaletteTokens.CodeText] = "#E2E8F0",
        [PaletteTokens.FallbackWash] = "rgba(2,6,23,.45)",
    }.ToFrozenDictionary());

    /// <summary>A copy of this palette with one token replaced.</summary>
    public BrandPalette With(string token, string value)
    {
        Dictionary<string, string> tokens = new(Tokens) { [token] = value };
        return new(tokens.ToFrozenDictionary());
    }
}
