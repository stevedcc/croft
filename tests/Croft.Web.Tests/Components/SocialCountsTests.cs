using AwesomeAssertions;
using Bunit;
using Croft.Shared.Contracts.Social;
using Croft.Web.Components;

namespace Croft.Web.Tests.Components;

/// <summary>Brief 17e item 5: the overlay pill with an announcement's likes, reposts and replies.</summary>
public class SocialCountsTests : BlogBunitContext
{
    private IRenderedComponent<SocialCounts> RenderCounts(SocialCountsDto? counts, string? size = null) =>
        Render<SocialCounts>(p =>
        {
            p.Add(x => x.Counts, counts);
            if (size is not null)
                p.Add(x => x.Size, size);
        });

    [Fact]
    public void NoCounts_RendersNothing() =>
        RenderCounts(null).Markup.Should().BeEmpty("a post with no counts shows no pill");

    [Fact]
    public void TheGroup_IsNamedByItsExactCounts()
    {
        var group = RenderCounts(new SocialCountsDto(12, 3, 4)).Find("span.social-counts");

        group.GetAttribute("role").Should().Be("group");
        group.GetAttribute("aria-label").Should().Be("12 likes, 3 reposts, 4 replies");
    }

    [Fact]
    public void OneOfEach_UsesTheSingularInTheLabel() =>
        RenderCounts(new SocialCountsDto(1, 1, 1)).Find(".social-counts").GetAttribute("aria-label")
            .Should().Be("1 like, 1 repost, 1 reply");

    [Fact]
    public void TheThreeNumbers_AreCompact_InLikesRepostsRepliesOrder()
    {
        var cut = RenderCounts(new SocialCountsDto(63724, 1200, 7));

        cut.FindAll(".social-counts__item").Select(i => i.TextContent.Trim()).Should().Equal("63.7k", "1.2k", "7");
        cut.Find(".social-counts").GetAttribute("aria-label").Should().Be("63724 likes, 1200 reposts, 7 replies", "the label keeps the exact numbers");
    }

    [Fact]
    public void EachItem_HasADecorativeIcon()
    {
        var icons = RenderCounts(new SocialCountsDto(1, 2, 3)).FindAll(".social-counts__item svg");

        icons.Should().HaveCount(3);
        icons.Should().OnlyContain(svg => svg.GetAttribute("aria-hidden") == "true" && svg.GetAttribute("fill") == "currentColor" && svg.QuerySelector("path") != null);
    }

    [Fact]
    public void TheCompactSize_AddsItsModifierClass()
    {
        RenderCounts(new SocialCountsDto(1, 2, 3), "compact").Find(".social-counts").ClassList.Should().Contain("social-counts--compact");
        RenderCounts(new SocialCountsDto(1, 2, 3)).Find(".social-counts").ClassList.Should().NotContain("social-counts--compact");
    }
}
