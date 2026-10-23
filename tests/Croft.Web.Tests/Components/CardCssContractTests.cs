using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace Croft.Web.Tests.Components;

/// <summary>
/// Task R3.2: pins the plan's exact CSS values (geometry, clamps, the fallback banner tile) in
/// the components' scoped stylesheets. bUnit has no layout engine and Task R3.6's E2E only
/// measures overflow at 390px, so this is where the brief's verbatim values (aspect-ratio 21/9
/// and 4/3, max-height 420px, List image 34% min 96px, -webkit-line-clamp 3/2, fallback tiled at
/// 180px under --fallback-wash) are held against drift. The fallback's image URL is theme.css's
/// --fallback-banner token (asserted in BannerOrFallbackTests), so the CSS carries the tile
/// geometry only.
/// </summary>
public class CardCssContractTests
{
    private static string Css(string fileName) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src", "Croft.Web", "Components", "Blog", fileName));

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

    [Fact]
    public void PostCardCss_FeaturedImageArea_UsesBriefValues()
    {
        var css = Css("PostCard.razor.css");

        css.Should().MatchRegex(@"aspect-ratio:\s*21\s*/\s*9\s*;");
        css.Should().MatchRegex(@"max-height:\s*420px\s*;");
        css.Should().MatchRegex(@"aspect-ratio:\s*4\s*/\s*3\s*;", "the phone ratio is 4/3 (plan key rules)");
    }

    [Fact]
    public void PostCardCss_ListImage_Uses34PercentMin96px()
    {
        var css = Css("PostCard.razor.css");

        css.Should().MatchRegex(@"34%");
        css.Should().MatchRegex(@"96px");
    }

    [Fact]
    public void PostCardCss_Summaries_AreLineClamped()
    {
        var css = Css("PostCard.razor.css");

        Regex.Matches(css, @"-webkit-line-clamp:\s*3\s*;").Should().HaveCount(1, "the Featured summary is clamped to 3 lines");
        Regex.Matches(css, @"-webkit-line-clamp:\s*2\s*;").Should().HaveCount(c => c >= 2, "the List summary is clamped to 2 lines (titles/subtitles may also clamp)");
    }

    [Fact]
    public void BannerOrFallbackCss_Fallback_UsesBriefValues()
    {
        var css = Css("BannerOrFallback.razor.css");

        css.Should().Contain("background-size: 180px", "the fallback tile is 180px");
        css.Should().Contain("background-repeat: repeat", "the fallback repeats as a tile");
        css.Should().Contain("var(--fallback-wash)");
    }
}
