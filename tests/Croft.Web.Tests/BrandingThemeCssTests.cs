using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace Croft.Web.Tests;

/// <summary>
/// The generated theme.css (brief 07a item 2): the three colour blocks app.css held —
/// <c>:root</c>, <c>:root[data-theme=dark]</c> and the prefers-color-scheme block — with every
/// one of the 18 tokens once per block, from a known palette. app.css itself keeps no colour
/// values beyond its two marked carve-out regions.
/// </summary>
public class BrandingThemeCssTests
{
    /// <summary>A palette where every token's value is unique across tokens and modes.</summary>
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

    private static string ThemeCss() => new BrandingService(KnownBranding()).ThemeCss;

    [Fact]
    public void ThemeCss_HasTheThreeBlocksAppCssHeld()
    {
        var css = ThemeCss();

        css.Should().Contain(":root {", "the light values live on :root");
        css.Should().Contain(":root[data-theme=dark] {", "the explicit dark theme gets its own block");
        css.Should().Contain("@media (prefers-color-scheme: dark) {");
        css.Should().Contain(":root:not([data-theme=light]) {", "system dark applies only without an explicit light choice");
    }

    [Fact]
    public void ThemeCss_ContainsEachTokenOncePerBlockWithItsValue()
    {
        var css = ThemeCss();
        var branding = KnownBranding();

        for (var i = 0; i < PaletteTokens.All.Count; i++)
        {
            var token = PaletteTokens.All[i];
            Regex.Matches(css, Regex.Escape($"--{token}: {branding.Light[token]};"))
                .Should().HaveCount(1, $"--{token} (light) belongs to the :root block alone");
            Regex.Matches(css, Regex.Escape($"--{token}: {branding.Dark[token]};"))
                .Should().HaveCount(2, $"--{token} (dark) belongs to both dark blocks");
            Regex.Matches(css, Regex.Escape($"--{token}:"))
                .Should().HaveCount(3, $"--{token} must be defined exactly once per block");
        }
    }

    [Fact]
    public void ThemeCss_DefinesTheFallbackWash()
    {
        var css = ThemeCss();

        css.Should().Contain("--fallback-wash:", "the fallback banner's wash is one of the 18 tokens");
    }

    [Fact]
    public void ThemeCss_DefinesTheFallbackBannerUrl_AsATokenNotAnInlineStyle()
    {
        // The fallback banner's URL carries its content-hash cache-buster and changes with the
        // site's file — as a token in theme.css the banner markup needs no style attribute
        // (the public policy bans them: style-src-attr 'none').
        var branding = Branding.Defaults with
        {
            FileHashes = new Dictionary<string, string> { ["fallback-banner.png"] = "pnghash" },
            FallbackBannerFile = "fallback-banner.png",
        };
        var css = new BrandingService(branding).ThemeCss;

        css.Should().Contain("--fallback-banner: url('/branding/fallback-banner.png?v=pnghash');",
            "the class paints the fallback with var(--fallback-banner), so the URL belongs to theme.css");
        Regex.Matches(css, "--fallback-banner:").Should().HaveCount(1,
            "the fallback banner does not change with the colour mode: one declaration on :root");
    }

    [Fact]
    public void AppCss_KeepsNoColourValues()
    {
        // app.css's exceptions are the three marked regions: MudBlazor's scrollbar rules, copied
        // verbatim (their byte-exactness is MudThemeStyleCspTests' assertion), the drawer
        // scrim's fixed alpha constants and the error-text colours (W-095). Every colour value outside those regions still comes
        // from theme.css — in any colour syntax, not just hex and rgb().
        var css = ErrorTextColourValues.Split(ScrimColourValues.Split(
            MudBlazorScrollbarCopy.Split(
                File.ReadAllText(Path.Combine(RepoRoot(), "src", "Croft.Web", "wwwroot", "app.css"))).Remainder).Remainder).Remainder;

        css.Should().NotMatchRegex(@"#[0-9A-Fa-f]{3,8}\b", "every colour value outside the marked carve-out regions comes from theme.css");
        css.Should().NotMatchRegex(@"(?<![A-Za-z])rgba?\(", "every colour value outside the marked carve-out regions comes from theme.css");
        css.Should().NotMatchRegex(@"(?<![A-Za-z])color\(", "every colour value outside the marked carve-out regions comes from theme.css");
        css.Should().NotMatchRegex(@"(?<![A-Za-z])color-mix\(", "every colour value outside the marked carve-out regions comes from theme.css");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Croft.slnx")))
        {
            dir = dir.Parent!;
        }

        dir.Should().NotBeNull("the tests run from inside the repository");
        return dir!.FullName;
    }
}
