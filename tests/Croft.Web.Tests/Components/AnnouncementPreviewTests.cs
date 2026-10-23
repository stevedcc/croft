using AwesomeAssertions;
using Bunit;
using Croft.Shared.Contracts.Social;
using Croft.Shared.Social;
using Croft.Web.Components.Admin;

namespace Croft.Web.Tests.Components;

/// <summary>Brief 12e item 5: a network's preview of the default text carries the hashtags its sender will add, and counts them.</summary>
public class AnnouncementPreviewTests : BlogBunitContext
{
    private const string Url = "https://blog.example/post/a-post";

    private IRenderedComponent<AnnouncementPreview> RenderPreview(SocialPlatform platform, string title, string? subtitle, string? text, params string[] hashtags) =>
        Render<AnnouncementPreview>(p => p
            .Add(x => x.Platform, platform)
            .Add(x => x.Title, title)
            .Add(x => x.Subtitle, subtitle)
            .Add(x => x.Text, text)
            .Add(x => x.PostUrl, Url)
            .Add(x => x.Hashtags, hashtags));

    [Theory]
    [InlineData(SocialPlatform.Mastodon)]
    [InlineData(SocialPlatform.Bluesky)]
    public void TheDefault_WithHashtags_ShowsAndCountsExactlyWhatTheSharedFittingGives(SocialPlatform platform)
    {
        string[] hashtags = ["#Homelab", "#SelfHosted"];
        var expected = AnnouncementText.Compose(AnnouncementText.FitDefault(platform, "Title", "Sub", hashtags, Url), Url);

        var cut = RenderPreview(platform, "Title", "Sub", null, hashtags);

        cut.Find(".announce-preview__text").TextContent.Should().Be(expected).And.Be($"Title\n\nSub\n\n#Homelab #SelfHosted\n\n{Url}");
        cut.Find(".announce-preview__count").TextContent.Trim().Should().Be($"{AnnouncementText.Length(platform, expected)} / {AnnouncementText.Limit(platform)}");
    }

    [Fact]
    public void ALongDefault_KeepsTheHashtags_AndCutsTheSubtitle()
    {
        string[] hashtags = ["#Homelab", "#SelfHosted", "#DotNet"];

        var cut = RenderPreview(SocialPlatform.Bluesky, new string('T', 40), new string('S', 214), null, hashtags);

        cut.Find(".announce-preview__text").TextContent.Should().EndWith($"…\n\n#Homelab #SelfHosted #DotNet\n\n{Url}");
        cut.Find(".announce-preview__count").TextContent.Trim().Should().Be("300 / 300");
    }

    [Fact]
    public void ACustomText_GetsNoHashtags()
    {
        var cut = RenderPreview(SocialPlatform.Mastodon, "Title", "Sub", "My words", "#Homelab");

        cut.Find(".announce-preview__text").TextContent.Should().Be($"My words\n\n{Url}");
    }

    [Fact]
    public void WithoutHashtags_TheTextIsToday()
    {
        var cut = RenderPreview(SocialPlatform.Mastodon, "Title", "Sub", null);

        cut.Find(".announce-preview__text").TextContent.Should().Be($"Title\n\nSub\n\n{Url}");
    }
}
