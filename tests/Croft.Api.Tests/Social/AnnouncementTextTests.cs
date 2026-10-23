using AwesomeAssertions;
using Croft.Shared.Contracts.Social;
using Croft.Shared.Social;

namespace Croft.Api.Tests.Social;

/// <summary>Brief 12d item 1: how the two networks count an announcement, and the default text that is shortened to fit.</summary>
public class AnnouncementTextTests
{
    private const string Url = "https://blog.example/post/a-post";

    // Family emoji: man + ZWJ + woman + ZWJ + girl + ZWJ + boy = one grapheme cluster, 11 UTF-16 chars.
    private const string Family = "\U0001F468‍\U0001F469‍\U0001F467‍\U0001F466";

    [Fact]
    public void Limits_AreFiveHundredAndThreeHundred()
    {
        AnnouncementText.Limit(SocialPlatform.Mastodon).Should().Be(500);
        AnnouncementText.Limit(SocialPlatform.Bluesky).Should().Be(300);
    }

    [Theory]
    [InlineData("Hello", 5)]
    [InlineData("", 0)]
    [InlineData("é", 1)]                  // e + combining acute: one cluster
    [InlineData("äöü", 3)]    // three letters with diaeresis
    [InlineData("日本語のテキスト", 8)]            // CJK: one each
    [InlineData(Family, 1)]                      // a ZWJ sequence is one cluster
    [InlineData("a" + Family + "b", 3)]
    [InlineData("\U0001F600\U0001F600", 2)]      // two emoji, four UTF-16 chars
    public void BothNetworks_CountGraphemeClusters(string text, int expected)
    {
        AnnouncementText.BlueskyLength(text).Should().Be(expected);
        AnnouncementText.MastodonLength(text).Should().Be(expected);
    }

    [Fact]
    public void Mastodon_CountsAnyUrlAs23_WhateverItsLength()
    {
        var shortUrl = "https://a.example/x";
        var longUrl = "https://a-very-long-host-name.example/post/" + new string('p', 150);

        AnnouncementText.MastodonLength(shortUrl).Should().Be(23);
        AnnouncementText.MastodonLength(longUrl).Should().Be(23);
        AnnouncementText.MastodonLength($"Hi\n\n{longUrl}").Should().Be(2 + 2 + 23);
        AnnouncementText.MastodonLength($"{shortUrl} and {longUrl}").Should().Be(23 + 5 + 23);
    }

    [Fact]
    public void Bluesky_CountsTheUrlAsWritten()
    {
        var longUrl = "https://a-very-long-host-name.example/post/" + new string('p', 150);

        AnnouncementText.BlueskyLength(longUrl).Should().Be(longUrl.Length);
        AnnouncementText.BlueskyLength($"Hi\n\n{longUrl}").Should().Be(4 + longUrl.Length);
    }

    [Theory]
    [InlineData("https://blog.example", "a-post", "https://blog.example/post/a-post")]
    [InlineData("https://blog.example/", "a-post", "https://blog.example/post/a-post")]
    [InlineData(" https://blog.example// ", "a-post", "https://blog.example/post/a-post")]
    [InlineData("", "a-post", "/post/a-post")]
    [InlineData(null, "a-post", "/post/a-post")]
    public void PostUrl_IsTheBaseAddressWithoutItsTrailingSlash_AndThePostPath(string? baseUrl, string slug, string expected)
    {
        AnnouncementText.PostUrl(baseUrl, slug).Should().Be(expected);
    }

    [Fact]
    public void Compose_PutsTheAddressAfterABlankLine()
    {
        AnnouncementText.Compose("Body", Url).Should().Be($"Body\n\n{Url}");
    }

    [Fact]
    public void Default_IsTheTitle_ThenABlankLineAndTheSubtitle()
    {
        AnnouncementText.Default("Title", "Sub").Should().Be("Title\n\nSub");
        AnnouncementText.Default("Title", null).Should().Be("Title");
        AnnouncementText.Default("Title", "  ").Should().Be("Title");
    }

    [Theory]
    [InlineData(SocialPlatform.Mastodon)]
    [InlineData(SocialPlatform.Bluesky)]
    public void FitDefault_LeavesADefaultThatFitsAlone(SocialPlatform platform)
    {
        AnnouncementText.FitDefault(platform, "Title", "Sub", Url).Should().Be("Title\n\nSub");
    }

    [Theory]
    [InlineData(SocialPlatform.Mastodon)]
    [InlineData(SocialPlatform.Bluesky)]
    public void FitDefault_ShortensALongSubtitleWithAnEllipsis_ToExactlyTheLimit(SocialPlatform platform)
    {
        var body = AnnouncementText.FitDefault(platform, "Title", new string('s', 600), Url);

        body.Should().StartWith("Title\n\nsss").And.EndWith("…");
        AnnouncementText.Length(platform, AnnouncementText.Compose(body, Url)).Should().Be(AnnouncementText.Limit(platform));
    }

    [Fact]
    public void FitDefault_ShortensTheTitle_WhenTheTitleAloneDoesNotFit()
    {
        var body = AnnouncementText.FitDefault(SocialPlatform.Bluesky, new string('t', 400), "A subtitle", Url);

        body.Should().StartWith("ttt").And.EndWith("…").And.NotContain("subtitle");
        AnnouncementText.BlueskyLength(AnnouncementText.Compose(body, Url)).Should().Be(300);
    }

    [Fact]
    public void FitDefault_WithNoSubtitle_ShortensALongTitle()
    {
        var body = AnnouncementText.FitDefault(SocialPlatform.Bluesky, new string('t', 400), null, Url);

        body.Should().EndWith("…");
        AnnouncementText.BlueskyLength(AnnouncementText.Compose(body, Url)).Should().Be(300);
    }

    [Fact]
    public void FitDefault_NeverCutsAGraphemeCluster()
    {
        var subtitle = string.Concat(Enumerable.Repeat(Family, 400));

        var body = AnnouncementText.FitDefault(SocialPlatform.Bluesky, "T", subtitle, Url);

        AnnouncementText.BlueskyLength(AnnouncementText.Compose(body, Url)).Should().Be(300);
        body.Replace("T\n\n", "").TrimEnd('…').Should().MatchRegex("^(\U0001F468‍\U0001F469‍\U0001F467‍\U0001F466)+$");
    }

    [Fact]
    public void FitDefault_ForMastodon_CountsTheUrlAs23()
    {
        var longUrl = "https://a-very-long-host-name.example/post/" + new string('p', 150);

        var body = AnnouncementText.FitDefault(SocialPlatform.Mastodon, "T", new string('s', 600), longUrl);

        AnnouncementText.MastodonLength(AnnouncementText.Compose(body, longUrl)).Should().Be(500);
    }
}
