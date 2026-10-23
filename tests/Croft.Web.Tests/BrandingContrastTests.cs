using AwesomeAssertions;

namespace Croft.Web.Tests;

/// <summary>
/// The WCAG 2 contrast maths behind the startup warnings: the relative-luminance formula against
/// known pairs (black on white = 21.00, #777777 on white = 4.48) and the built-in default palette
/// passing every checked pair in both modes.
/// </summary>
public class BrandingContrastTests
{
    [Fact]
    public void BlackOnWhite_Is21To1()
    {
        BrandingContrast.Ratio("#000000", "#FFFFFF").Should().BeApproximately(21.00, 0.005);
    }

    [Fact]
    public void Grey777OnWhite_Is448To1()
    {
        BrandingContrast.Ratio("#777777", "#FFFFFF").Should().BeApproximately(4.48, 0.005);
    }

    [Theory]
    [InlineData("text", "bg")]
    [InlineData("text", "surface")]
    [InlineData("muted", "bg")]
    [InlineData("primary", "surface")]
    [InlineData("on-primary", "primary")]
    [InlineData("on-secondary-container", "secondary-container")]
    [InlineData("sidebar-text", "sidebar")]
    public void DefaultPalette_EveryCheckedPairPassesAa_InBothModes(string foreground, string background)
    {
        foreach (var (mode, palette) in new[] { ("light", BrandPalette.DefaultLight), ("dark", BrandPalette.DefaultDark) })
        {
            var ratio = BrandingContrast.Ratio(palette[foreground], palette[background]);
            ratio.Should().BeGreaterThanOrEqualTo(4.5, $"{foreground}/{background} must pass AA in {mode} (got {ratio:0.00}:1)");
        }
    }
}
