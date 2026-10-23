using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace Croft.Web.Tests;

/// <summary>
/// Brief 13g item 7 (review I-8, I-9): the palette guard covers what the stylesheets combine. The
/// list of foreground/background pairs that site palettes are checked against must hold every pair
/// the CSS puts together, and the colours the CSS fixes by hand (the search hairline, the hero
/// subtitle, MudBlazor's error red) must reach their ratios on the default backgrounds.
/// </summary>
public sealed partial class PaletteGuardTests
{
    private static readonly string Root = RepoRoot();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Croft.slnx")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("the tests run from inside the repository");
        return dir!.FullName;
    }

    private static IEnumerable<string> StyleSheets()
    {
        var web = Path.Combine(Root, "src", "Croft.Web");
        yield return Path.Combine(web, "wwwroot", "app.css");
        foreach (var file in Directory.EnumerateFiles(Path.Combine(web, "Components"), "*.razor.css", SearchOption.AllDirectories))
        {
            yield return file;
        }
    }

    [GeneratedRegex(@"([^{}]*)\{([^{}]*)\}")]
    private static partial Regex RuleRegex();

    [GeneratedRegex(@"(?<![-\w])color\s*:\s*([^;]+)")]
    private static partial Regex ColorDeclaration();

    [GeneratedRegex(@"background(?:-color)?\s*:\s*([^;]+)")]
    private static partial Regex BackgroundDeclaration();

    [GeneratedRegex(@"var\(--([\w-]+)")]
    private static partial Regex VarUse();

    /// <summary>Foreground and background tokens one rule puts together, per stylesheet: a literal white
    /// foreground counts as <see cref="PaletteTokens.FixedWhite"/>.</summary>
    private static IEnumerable<(string File, string Selector, string Foreground, string Background)> CombinedPairs()
    {
        foreach (var file in StyleSheets())
        {
            var css = Regex.Replace(File.ReadAllText(file), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            foreach (Match rule in RuleRegex().Matches(css))
            {
                var foregrounds = ColorDeclaration().Matches(rule.Groups[2].Value)
                    .SelectMany(m => ForegroundTokens(m.Groups[1].Value)).ToList();
                var backgrounds = BackgroundDeclaration().Matches(rule.Groups[2].Value)
                    .SelectMany(m => VarUse().Matches(m.Groups[1].Value).Select(v => v.Groups[1].Value))
                    .Where(PaletteTokens.All.Contains).ToList();
                foreach (var foreground in foregrounds)
                {
                    foreach (var background in backgrounds)
                    {
                        yield return (Path.GetFileName(file), rule.Groups[1].Value.Trim(), foreground, background);
                    }
                }
            }
        }
    }

    private static IEnumerable<string> ForegroundTokens(string value)
    {
        value = value.Trim();
        if (value.Equals("#FFFFFF", StringComparison.OrdinalIgnoreCase) || value.Equals("#fff", StringComparison.OrdinalIgnoreCase)
            || value.Equals("white", StringComparison.OrdinalIgnoreCase))
        {
            return [PaletteTokens.FixedWhite];
        }

        return VarUse().Matches(value).Select(v => v.Groups[1].Value).Where(PaletteTokens.All.Contains);
    }

    [Fact]
    public void EveryTokenPairTheCssCombinesInOneRule_IsInTheContrastList()
    {
        var listed = PaletteTokens.ContrastPairs.ToHashSet();
        var missing = CombinedPairs()
            .Where(p => !listed.Contains((p.Foreground, p.Background)))
            .Select(p => $"{p.Foreground} on {p.Background}  ({p.File}: {p.Selector.ReplaceLineEndings(" ")})")
            .Distinct().ToList();

        missing.Should().BeEmpty("a pair the stylesheets render must be checked against site palettes (PaletteTokens.ContrastPairs)");
    }

    [Fact]
    public void EveryForegroundTokenTheCssUses_IsCheckedAsAForegroundSomewhere()
    {
        // The pairs above catch a foreground and a background in one rule. A foreground set where the
        // background comes from an ancestor is only caught here: a token never used as a listed
        // foreground has no pair at all.
        var checkedForegrounds = PaletteTokens.ContrastPairs.Select(p => p.Foreground).ToHashSet();
        var used = StyleSheets()
            .SelectMany(f => ColorDeclaration().Matches(Regex.Replace(File.ReadAllText(f), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline)))
            .SelectMany(m => ForegroundTokens(m.Groups[1].Value))
            .Distinct().ToList();

        used.Where(t => !checkedForegrounds.Contains(t)).Should().BeEmpty();
    }

    [Fact]
    public void TheReviewsFivePairs_AreListed()
    {
        PaletteTokens.ContrastPairs.Should().Contain([
            (PaletteTokens.Muted, PaletteTokens.Surface),
            (PaletteTokens.Primary, PaletteTokens.Bg),
            (PaletteTokens.FixedWhite, PaletteTokens.PrimaryStrong),
            (PaletteTokens.OnSecondaryContainer, PaletteTokens.Bg),
            (PaletteTokens.Primary, PaletteTokens.PrimaryContainer),
        ]);
        PaletteTokens.NonTextContrastPairs.Should().Contain((PaletteTokens.Muted, PaletteTokens.Bg),
            "the borders of the editor's fields are boundaries and need 3:1");
    }

    private static IEnumerable<(string Name, BrandPalette Light, BrandPalette Dark)> Palettes()
    {
        yield return ("default", BrandPalette.DefaultLight, BrandPalette.DefaultDark);
        using var example = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "branding.example", "branding.json")));
        var palette = example.RootElement.GetProperty("palette");
        BrandPalette Merge(BrandPalette defaults, string mode)
        {
            var result = defaults;
            foreach (var token in palette.GetProperty(mode).EnumerateObject())
            {
                result = result.With(token.Name, token.Value.GetString()!);
            }

            return result;
        }

        yield return ("example", Merge(BrandPalette.DefaultLight, "light"), Merge(BrandPalette.DefaultDark, "dark"));
    }

    [Fact]
    public void TheDefaultAndExamplePalettes_PassEveryTextPairAt4Point5_AndEveryBoundaryAt3()
    {
        var failures = new List<string>();
        foreach (var (name, light, dark) in Palettes())
        {
            foreach (var (mode, palette) in new[] { ("light", light), ("dark", dark) })
            {
                foreach (var (list, required) in new[] { (PaletteTokens.ContrastPairs, 4.5), (PaletteTokens.NonTextContrastPairs, 3.0) })
                {
                    foreach (var (foreground, background) in list)
                    {
                        var ratio = BrandingContrast.Ratio(PaletteTokens.ValueOf(palette, foreground), PaletteTokens.ValueOf(palette, background));
                        if (ratio < required)
                        {
                            failures.Add($"{name} {mode}: {foreground} on {background} = {ratio:0.00}:1, needs {required}:1");
                        }
                    }
                }
            }
        }

        failures.Should().BeEmpty();
    }

    [Fact]
    public void TheSearchHairline_ReachesThreeToOne_OnTheDefaultBackgrounds()
    {
        var css = File.ReadAllText(Path.Combine(Root, "src", "Croft.Web", "Components", "Pages", "Search.razor.css"));
        var lines = FieldLine().Matches(css).ToList();
        lines.Should().HaveCount(3, "light, explicit dark and system dark");

        string Composite(Match line, string background)
        {
            var alpha = line.Groups[4].Success ? double.Parse(line.Groups[4].Value, CultureInfo.InvariantCulture) : 1;
            BrandingColor.TryParse(background, out var r, out var g, out var b, out _).Should().BeTrue();
            string Channel(string value, double below) =>
                ((int)Math.Round(double.Parse(value, CultureInfo.InvariantCulture) * alpha + below * (1 - alpha))).ToString("X2", CultureInfo.InvariantCulture);
            return "#" + Channel(line.Groups[1].Value, r * 255) + Channel(line.Groups[2].Value, g * 255) + Channel(line.Groups[3].Value, b * 255);
        }

        var bgLight = BrandPalette.DefaultLight[PaletteTokens.Bg];
        var bgDark = BrandPalette.DefaultDark[PaletteTokens.Bg];
        BrandingContrast.Ratio(Composite(lines[0], bgLight), bgLight).Should().BeGreaterThanOrEqualTo(3, "the light hairline on the light background");
        BrandingContrast.Ratio(Composite(lines[1], bgDark), bgDark).Should().BeGreaterThanOrEqualTo(3, "the explicit dark hairline");
        BrandingContrast.Ratio(Composite(lines[2], bgDark), bgDark).Should().BeGreaterThanOrEqualTo(3, "the system dark hairline");
    }

    [GeneratedRegex(@"--field-line:\s*rgb\((\d+) (\d+) (\d+)(?: / ([\d.]+))?\)")]
    private static partial Regex FieldLine();

    [Fact]
    public void TheHeroSubtitle_IsOpaqueWhite_AndClearsAaOverTheWorstCaseBanner()
    {
        var css = File.ReadAllText(Path.Combine(Root, "src", "Croft.Web", "Components", "Blog", "PostHero.razor.css"));
        var subtitle = Regex.Match(css, @"\.post-hero__subtitle\s*\{[^}]*?(?<![-\w])color:\s*([^;]+);");
        subtitle.Success.Should().BeTrue();
        subtitle.Groups[1].Value.Trim().Should().BeOneOf(["#FFFFFF", "#fff", "white"], "an opaque white: at 85% it fell to 3.94:1");

        // The backdrop of the text block's top edge is the gradient's alpha over the lightest banner (white).
        var gradient = Regex.Match(css, @"linear-gradient\(.*rgba\(0, 0, 0, \.55\)\s*4rem");
        gradient.Success.Should().BeTrue("the gradient's mid-stop sits at the text block's top");
        var backdrop = ((int)Math.Round(255 * (1 - 0.55))).ToString("X2", CultureInfo.InvariantCulture);
        BrandingContrast.Ratio("#FFFFFF", "#" + backdrop + backdrop + backdrop).Should().BeGreaterThanOrEqualTo(4.5,
            "white over the 55% black gradient on a white banner");
    }

    [GeneratedRegex(@"--error-text:\s*(#[0-9A-Fa-f]{6})")]
    private static partial Regex ErrorText();

    [Fact]
    public void TheErrorTextColour_ReachesAaOnTheDefaultBackgroundAndSurface_InBothModes()
    {
        // MudBlazor's own error red (#F44336 light, #F64E62 dark) is 3.68:1 and 4.30:1 on the surface:
        // the Delete buttons failed axe's color-contrast. app.css colours them itself, for dialogs too (W-095).
        var css = File.ReadAllText(Path.Combine(Root, "src", "Croft.Web", "wwwroot", "app.css"));
        var values = ErrorText().Matches(css).Select(m => m.Groups[1].Value).ToList();
        values.Should().HaveCount(3, "light, explicit dark and system dark");

        foreach (var (mode, value, tokens) in new[]
        {
            ("light", values[0], BrandPalette.DefaultLight), ("dark", values[1], BrandPalette.DefaultDark), ("system dark", values[2], BrandPalette.DefaultDark),
        })
        {
            foreach (var surface in new[] { PaletteTokens.Bg, PaletteTokens.Surface })
            {
                BrandingContrast.Ratio(value, tokens[surface]).Should().BeGreaterThanOrEqualTo(4.5, $"{mode} error text {value} on {surface}");
            }
        }
    }
}
