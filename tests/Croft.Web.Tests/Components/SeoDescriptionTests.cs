using Croft.Web.Components.Layout;
using AwesomeAssertions;

namespace Croft.Web.Tests.Components;

/// <summary>
/// The three-level meta-description fallback (brief 03e item 1): a post's Excerpt wins, then
/// its Subtitle, then the start of its summary text — plain text, about 160 characters, cut at
/// a word. The rule lives in one helper so the post page's head and the feed describe posts
/// the same way.
/// </summary>
public class SeoDescriptionTests
{
    [Fact]
    public void ReturnsExcerpt_WhenPresent()
    {
        SeoDescription.FromPost("The excerpt.", "The subtitle.", "Summary start.")
            .Should().Be("The excerpt.");
    }

    [Fact]
    public void ReturnsSubtitle_WhenExcerptMissing()
    {
        SeoDescription.FromPost(null, "The subtitle.", "Summary start.")
            .Should().Be("The subtitle.");
    }

    [Fact]
    public void ReturnsSubtitle_WhenExcerptIsBlank()
    {
        SeoDescription.FromPost("   ", "The subtitle.", "Summary start.")
            .Should().Be("The subtitle.", "a whitespace-only excerpt is no excerpt");
    }

    [Fact]
    public void FallsBackToSummaryStart_CutAtWordAbout160Characters()
    {
        // 10-char words, space separated: the first 14 span 153 characters, the 15th would
        // cross 160, so the cut lands after the 14th and appends an ellipsis.
        var words = Enumerable.Range(0, 20).Select(i => new string((char)('a' + i), 10)).ToArray();

        var description = SeoDescription.FromPost(null, null, string.Join(' ', words));

        description.Should().Be(string.Join(' ', words.Take(14)) + "…");
        description.Length.Should().BeLessThanOrEqualTo(160 + 1, "about 160 characters plus the ellipsis");
    }

    [Fact]
    public void KeepsSummaryAsIs_WhenAtMost160Characters()
    {
        var summary = new string('a', 160);

        SeoDescription.FromPost(null, null, summary).Should().Be(summary, "no cut, no ellipsis");
    }

    [Fact]
    public void HardCutsASummary_WithNoSpaceBefore160Characters()
    {
        var summary = new string('a', 300);

        SeoDescription.FromPost(null, null, summary)
            .Should().Be(new string('a', 160) + "…", "one giant word: cut at 160 rather than mid-search for a space");
    }

    [Fact]
    public void ReturnsEmpty_WhenEverythingIsEmpty()
    {
        SeoDescription.FromPost(null, " ", "").Should().BeEmpty();
    }
}
