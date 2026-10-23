using AwesomeAssertions;
using Croft.Shared.Contracts.Social;
using Croft.Shared.Social;

namespace Croft.Api.Tests.Social;

/// <summary>Brief 12e item 2: which hashtags the default announcement text gets, and how the text is fitted with them.</summary>
public class AnnouncementHashtagsTests
{
    // 32 graphemes. Bluesky counts it as written, Mastodon as 23. The composed text adds "\n\n" (2) in front of it.
    private const string Url = "https://blog.example/post/a-post";

    private static HashtagDto Both(string name) => new(name, HashtagNetworks.Both);

    private static readonly HashtagDto[] Category =
        [Both("Homelab"), new("SelfHosted", HashtagNetworks.BlueskyOnly)];

    private static readonly HashtagDto[][] Tags =
    [
        [Both("DotNet")],
        [Both("homelab"), new("Kubernetes", HashtagNetworks.MastodonOnly)],
    ];

    [Fact]
    public void SelectHashtags_Mastodon_TakesCategoryFirstThenTags_DropsOtherNetworksAndCaseDuplicates()
    {
        AnnouncementText.SelectHashtags(SocialPlatform.Mastodon, Category, Tags)
            .Should().Equal("#Homelab", "#DotNet", "#Kubernetes");
    }

    [Fact]
    public void SelectHashtags_Bluesky_StopsAtThree()
    {
        // Homelab, SelfHosted (category); DotNet (tag 1); the tags' homelab is a duplicate and Kubernetes is Mastodon only.
        AnnouncementText.SelectHashtags(SocialPlatform.Bluesky, Category, Tags)
            .Should().Equal("#Homelab", "#SelfHosted", "#DotNet");
    }

    [Fact]
    public void SelectHashtags_CapsAtThree_EvenWhenMoreApply()
    {
        HashtagDto[] category = [Both("A1"), Both("B1"), Both("C1"), Both("D1")];

        AnnouncementText.SelectHashtags(SocialPlatform.Mastodon, category, [[Both("E1")]])
            .Should().Equal("#A1", "#B1", "#C1");
    }

    [Fact]
    public void SelectHashtags_WithoutACategory_UsesTheTagsOnly()
    {
        AnnouncementText.SelectHashtags(SocialPlatform.Bluesky, [], Tags)
            .Should().Equal("#DotNet", "#homelab");
    }

    [Fact]
    public void SelectHashtags_WithNoHashtagsAnywhere_IsEmpty()
    {
        AnnouncementText.SelectHashtags(SocialPlatform.Mastodon, [], [[], []]).Should().BeEmpty();
    }

    [Fact]
    public void Default_WithHashtags_PutsThemAfterTheSubtitleOnTheirOwnParagraph()
    {
        AnnouncementText.Default("Title", "Sub", ["#A1", "#B1"]).Should().Be("Title\n\nSub\n\n#A1 #B1");
        AnnouncementText.Default("Title", null, ["#A1", "#B1"]).Should().Be("Title\n\n#A1 #B1");
    }

    [Fact]
    public void Default_WithoutHashtags_IsExactlyTheTwoArgumentDefault()
    {
        AnnouncementText.Default("Title", "Sub", []).Should().Be(AnnouncementText.Default("Title", "Sub")).And.Be("Title\n\nSub");
        AnnouncementText.Default("Title", null, []).Should().Be("Title");
    }

    // Bluesky, limit 300. URL 32 + "\n\n" 2 = 34, so the body may have 266 graphemes.
    // Hashtags "#Homelab #SelfHosted #DotNet" = 8 + 1 + 11 + 1 + 7 = 28.
    // Title 40 + "\n\n" 2 + subtitle 214 + "\n\n" 2 + 28 = 286 -> composed 320 > 300.
    // Cutting the subtitle to k graphemes plus "…": 40 + 2 + (k + 1) + 2 + 28 = 73 + k <= 266 -> k = 193, composed exactly 300.
    [Fact]
    public void FitDefault_Bluesky_CutsTheSubtitleFirst_AndKeepsAllThreeHashtags()
    {
        var title = new string('T', 40);
        var subtitle = new string('S', 214);
        string[] hashtags = ["#Homelab", "#SelfHosted", "#DotNet"];
        AnnouncementText.BlueskyLength(AnnouncementText.Compose(AnnouncementText.Default(title, subtitle, hashtags), Url)).Should().Be(320);

        var body = AnnouncementText.FitDefault(SocialPlatform.Bluesky, title, subtitle, hashtags, Url);

        body.Should().Be($"{title}\n\n{new string('S', 193)}…\n\n#Homelab #SelfHosted #DotNet");
        AnnouncementText.BlueskyLength(AnnouncementText.Compose(body, Url)).Should().Be(300);
    }

    // Title 250, subtitle "Sub": no subtitle length k >= 1 fits (250 + 2 + (k + 1) + 2 + 28 > 266), so the subtitle goes (step 3 needs 250 + 2 + 28 = 280 <= 266: no).
    // Step 4 drops hashtags from the end: two = 250 + 2 + 20 = 272 > 266; one = 250 + 2 + 8 = 260 <= 266.
    [Fact]
    public void FitDefault_Bluesky_WhenOnlyOneHashtagFits_KeepsExactlyTheFirst()
    {
        var title = new string('T', 250);

        var body = AnnouncementText.FitDefault(SocialPlatform.Bluesky, title, "Sub", ["#Homelab", "#SelfHosted", "#DotNet"], Url);

        body.Should().Be($"{title}\n\n#Homelab");
    }

    // Title 266 is the longest body that fits alone; no hashtag fits beside it (266 + 2 + 8 > 266), so step 5 gives the plain title.
    // A title of 300 is cut as today: the title and "…" must fit 266.
    [Fact]
    public void FitDefault_Bluesky_WhenNoHashtagFits_FallsBackToTheTitleAndCutsItLikeToday()
    {
        var fitting = new string('T', 266);
        AnnouncementText.FitDefault(SocialPlatform.Bluesky, fitting, "Sub", ["#Homelab"], Url).Should().Be(fitting);

        var long300 = new string('T', 300);
        AnnouncementText.FitDefault(SocialPlatform.Bluesky, long300, null, ["#Homelab"], Url)
            .Should().Be(new string('T', 265) + "…");
    }

    [Fact]
    public void FitDefault_Mastodon_CountsTheUrlAs23_SoTheSameTextFitsWhole()
    {
        var title = new string('T', 40);
        var subtitle = new string('S', 214);
        string[] hashtags = ["#Homelab", "#SelfHosted", "#DotNet"];
        // Whole: body 286 + 2 + 23 = 311 <= 500.

        var body = AnnouncementText.FitDefault(SocialPlatform.Mastodon, title, subtitle, hashtags, Url);

        body.Should().Be(AnnouncementText.Default(title, subtitle, hashtags));
        AnnouncementText.MastodonLength(AnnouncementText.Compose(body, Url)).Should().Be(311);
    }

    [Fact]
    public void FitDefault_WithoutHashtags_BehavesLikeTheFourArgumentOverload()
    {
        var title = new string('T', 40);
        var subtitle = new string('S', 400);

        AnnouncementText.FitDefault(SocialPlatform.Bluesky, title, subtitle, [], Url)
            .Should().Be(AnnouncementText.FitDefault(SocialPlatform.Bluesky, title, subtitle, Url));
    }
}
