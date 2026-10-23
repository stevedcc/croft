using AwesomeAssertions;
using Croft.Shared.Contracts.Social;
using Croft.Web.Components.Admin;

namespace Croft.Web.Tests;

/// <summary>Brief 12c item 3: the labels a hide writes onto its block, and the future form of <see cref="RelativeTime"/>.</summary>
public class SocialBlockLabelsTests
{
    private static DiscussionReplyDto Reply(string html, string name = "Ann", string handle = "ann@social.example") => new(
        SocialPlatform.Mastodon, "r1", handle, name, "https://social.example/@ann", "acct-1", null,
        DateTimeOffset.UnixEpoch, "https://social.example/@ann/r1", html, null, false, []);

    [Theory]
    [InlineData("<p>Plain text.</p>", "Plain text.")]
    [InlineData("<p>Hello <a href=\"https://example.org\" rel=\"nofollow ugc noopener\">world</a>, again</p>", "Hello world, again")]
    [InlineData("<p>First</p><p>Second</p>", "First Second")]
    [InlineData("<p>Line one<br>line two<br/>line three</p>", "Line one line two line three")]
    [InlineData("<p>  Many \n\n   spaces\t here </p>", "Many spaces here")]
    [InlineData("<p>Fish &amp; chips &lt;3</p>", "Fish & chips <3")]
    [InlineData("", "")]
    public void ForReply_StripsTagsDecodesEntitiesAndCollapsesWhitespace(string html, string expected) =>
        SocialBlockLabels.ForReply(Reply(html)).Should().Be($"@ann@social.example: {expected}");

    [Fact]
    public void ForReply_CutsAtEightyCharacters_AndAppendsAnEllipsisOnlyWhenCut()
    {
        SocialBlockLabels.ForReply(Reply($"<p>{new string('a', 80)}</p>")).Should().EndWith(": " + new string('a', 80));
        SocialBlockLabels.ForReply(Reply($"<p>{new string('a', 81)}</p>")).Should().EndWith(": " + new string('a', 80) + "…");
    }

    [Fact]
    public void ForReply_CountsCharactersNotUtf16Units_SoASurrogatePairIsNeverSplit()
    {
        var text = string.Concat(Enumerable.Repeat("😀", 81));

        var label = SocialBlockLabels.ForReply(Reply($"<p>{text}</p>"));

        label.Should().EndWith(": " + string.Concat(Enumerable.Repeat("😀", 80)) + "…");
    }

    [Fact]
    public void ForAccount_IsTheDisplayNameAndTheHandle() =>
        SocialBlockLabels.ForAccount(Reply("<p>x</p>", name: "Ann Example", handle: "ann@social.example"))
            .Should().Be("Ann Example (@ann@social.example)");

    [Theory]
    [InlineData(-30, "due now")]
    [InlineData(0, "due now")]
    [InlineData(59, "due now")]
    [InlineData(60, "in 1 minute")]
    [InlineData(10 * 60, "in 10 minutes")]
    [InlineData(60 * 60, "in 1 hour")]
    [InlineData(5 * 3600, "in 5 hours")]
    [InlineData(24 * 3600, "in 1 day")]
    [InlineData(3 * 24 * 3600, "in 3 days")]
    public void FormatUntil_ReadsTheWaitInTheLargestUnit(int seconds, string expected)
    {
        var now = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

        RelativeTime.FormatUntil(now.AddSeconds(seconds), now).Should().Be(expected);
    }
}
