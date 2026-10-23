using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Croft.Web.Components;
using Bunit;
using AwesomeAssertions;

namespace Croft.Web.Tests.Components;

/// <summary>
/// Task R3.4: related posts (spec §5.4) — up to 3 posts as thumbnail + title, linking to the
/// post pages. Review Focus 1: an empty list renders nothing at all.
/// </summary>
public class RelatedPostsTests : BlogBunitContext
{
    private static PostSummaryDto Summary(string title, string slug, BannerRefDto? banner = null) =>
        new(Guid.NewGuid(), slug, title, null, null, "Summary.", 5, PostStatus.Published,
            new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc), "An Author", null, [],
            banner, null, null);

    [Fact]
    public void RelatedPosts_RendersAtMostThreeWithThumbnailAndTitle()
    {
        var items = new[]
        {
            Summary("One", "one"), Summary("Two", "two"),
            Summary("Three", "three"), Summary("Four", "four"),
        };

        var cut = Render<RelatedPosts>(p => p.Add(x => x.Items, items));

        // Spec §5.4: "up to 3 posts (thumbnail + title)" — a fourth item is dropped.
        var links = cut.FindAll(".related-posts__link");
        links.Should().HaveCount(3);
        links[0].GetAttribute("href").Should().Be("/post/one");
        links[0].TextContent.Should().Contain("One");
        cut.FindAll(".related-posts__thumb").Should().HaveCount(3, "each item shows a thumbnail");
    }

    [Fact]
    public void RelatedPosts_NoItems_RendersNothing()
    {
        var cut = Render<RelatedPosts>(p => p.Add(x => x.Items, []));

        cut.Markup.Should().BeEmpty("an empty related list renders no panel content (Review Focus 1)");
    }

    [Fact]
    public void RelatedPosts_ItemWithoutBanner_ShowsFallbackBanner()
    {
        var cut = Render<RelatedPosts>(p => p.Add(x => x.Items, [Summary("One", "one")]));

        // A thumbnail without a banner uses the fallback banner (never a broken <img>).
        cut.Find(".banner-or-fallback__fallback");
    }
}
