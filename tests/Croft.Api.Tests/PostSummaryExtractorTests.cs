using Croft.Api.Services;
using AwesomeAssertions;

namespace Croft.Api.Tests;

public class PostSummaryExtractorTests
{
    [Fact]
    public void SkipsHeadingsAndUsesFirstParagraph() =>
        PostSummaryExtractor.Extract("# Title\n\nFirst *para* with [a link](x).\n\nSecond.").Summary
            .Should().Be("First para with a link.");

    [Fact]
    public void SkipsImageOnlyParagraph() =>
        PostSummaryExtractor.Extract("![alt](i.png)\n\nReal text.").Summary.Should().Be("Real text.");

    [Fact]
    public void SkipsCodeBlocks() =>
        PostSummaryExtractor.Extract("```cs\nvar x=1;\n```\n\nAfter code.").Summary.Should().Be("After code.");

    [Fact]
    public void CollapsesWhitespace() =>
        PostSummaryExtractor.Extract("Line one\nline   two.").Summary.Should().Be("Line one line two.");

    [Fact]
    public void TruncatesAtWordBoundaryWithEllipsis()
    {
        var s = PostSummaryExtractor.Extract(string.Join(' ', Enumerable.Repeat("word", 100))).Summary;
        s.Length.Should().BeLessThanOrEqualTo(301); s.Should().EndWith("…"); s.Should().NotContain("wor…");
    }

    [Fact]
    public void EmptyWhenNoParagraph() => PostSummaryExtractor.Extract("# Only heading").Summary.Should().BeEmpty();

    [Theory, InlineData(0, 1), InlineData(220, 1), InlineData(221, 2), InlineData(660, 3)]
    public void ReadingMinutes(int words, int expected) =>
        PostSummaryExtractor.Extract(string.Join(' ', Enumerable.Repeat("w", words))).ReadingMinutes.Should().Be(expected);

    [Fact]
    public void ReadingMinutesExcludesFencedCode() =>
        PostSummaryExtractor.Extract("```\n" + string.Join(' ', Enumerable.Repeat("w", 500)) + "\n```\n\nhi").ReadingMinutes.Should().Be(1);

    // The owner's paragraph shape (brief 10b): the 300-character cut lands exactly after a sentence
    // that ends in a full stop, so "…" appended after it read ".…".
    private const string ClusterParagraph =
        "I built my cluster from bare metal because I wanted to understand every layer, and nothing teaches " +
        "that like owning the failures. Every service on this page runs on hardware in my own house, and " +
        "when something breaks at night I am the one who fixes it, because I chose to run the whole stack " +
        "myself. Network, OS, Software.";

    [Fact]
    public void CutAtASentenceEnd_EndsThereWithoutEllipsis()
    {
        ClusterParagraph.Length.Should().BeGreaterThan(300, "the paragraph must be cut");
        PostSummaryExtractor.Extract(ClusterParagraph).Summary.Should().EndWith("run the whole stack myself.")
            .And.NotContain("…");
    }

    [Fact]
    public void CutInMidSentence_StillEndsInEllipsis()
    {
        var s = PostSummaryExtractor.Extract(string.Join(' ', Enumerable.Repeat("word", 100)) + ".").Summary;
        s.Should().EndWith("word…");
    }

    [Theory]
    [InlineData("?")]
    [InlineData("!")]
    [InlineData(".\"")]
    [InlineData("?)")]
    [InlineData(".\u201D")]
    [InlineData(".\u00AB")]
    [InlineData("!\u2018")]
    [InlineData("?}")]
    public void CutAfterOtherSentenceEnds_EndsThereWithoutEllipsis(string ending)
    {
        var markdown = string.Join(' ', Enumerable.Repeat("word", 58)) + ending + " " + new string('m', 60);
        var s = PostSummaryExtractor.Extract(markdown).Summary;
        s.Should().EndWith("word" + ending).And.NotContain("…");
    }
}
