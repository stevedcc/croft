using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Croft.Web.Components;
using Croft.Web.Components.Admin;
using Bunit;
using AwesomeAssertions;

namespace Croft.Web.Tests.Components;

/// <summary>
/// Task R3.4: the post hero (spec §5.4) — full content width, banner or fallback banner under a
/// dark gradient, with the category chip, h1 title, subtitle and date · reading time overlaid.
/// Review Focus 1 (missing optional data) applies: no banner/category/subtitle render cleanly.
/// </summary>
public class PostHeroTests : BlogBunitContext
{
    private static readonly CategoryDto Terrain =
        new(Guid.NewGuid(), "Travel", "travel", "Terrain", 3);

    private static PostDto Post(
        string title = "Rewriting my first Blazor app",
        string? subtitle = "What three years of technical debt taught me about components",
        BannerRefDto? banner = null,
        CategoryDto? category = null) =>
        new(Guid.NewGuid(), "rewriting-my-first-blazor-app", title, subtitle,
            "## Body\n\nText.", null, null, PostStatus.Published,
            new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc),
            "author-id", "Alice Author", null, [], category, 7);

    private IRenderedComponent<PostHero> RenderHero(PostDto post, bool draftRibbon = false) =>
        Render<PostHero>(p => p.Add(x => x.Post, post).Add(x => x.DraftRibbon, draftRibbon));

    [Fact]
    public void PostHero_NoBanner_Fallback()
    {
        var cut = RenderHero(Post(category: Terrain));

        cut.Find(".banner-or-fallback__fallback");
        var icon = cut.FindComponent<CategoryIcon>();
        icon.Instance.Name.Should().Be("Terrain", "the category icon decorates the fallback banner");
    }

    [Fact]
    public void PostHero_WithSocialCounts_ShowsThePillInsideTheHero()
    {
        var cut = RenderHero(Post() with { Social = new Croft.Shared.Contracts.Social.SocialCountsDto(12, 5, 5) });

        cut.Find(".post-hero > .social-counts").GetAttribute("aria-label").Should().Be("12 likes, 5 reposts, 5 replies");
    }

    [Fact]
    public void PostHero_WithoutSocialCounts_ShowsNoPill() =>
        RenderHero(Post()).FindAll(".social-counts").Should().BeEmpty("the editor preview and draft review pass a post without counts");

    [Fact]
    public void PostHero_DraftRibbon_WhenRequested()
    {
        var withRibbon = RenderHero(Post(), draftRibbon: true);
        withRibbon.Find(".post-hero__ribbon").TextContent.Should().Be("Draft");

        var withoutRibbon = RenderHero(Post(), draftRibbon: false);
        withoutRibbon.Markup.Should().NotContain("post-hero__ribbon", "the public page shows no draft ribbon");
    }

    [Fact]
    public void PostHero_DateLine_ShowsUpdated_WhenEditedOnALaterUtcDay()
    {
        // The hero's date line carries the same "Updated" rule as every date line (brief 07g
        // item 3), fed from the post's ContentEditedAt.
        var cut = RenderHero(Post() with
        {
            ContentEditedAt = new DateTime(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc)
        });

        cut.Find(".post-meta__when").TextContent
            .Should().Be("Published on 2 October 2026, last updated on 5 October 2026 · 7 min read");
    }

    [Fact]
    public void PostHero_DateLine_ShowsNoUpdated_WhenEditedTheSameUtcDay()
    {
        var cut = RenderHero(Post() with
        {
            ContentEditedAt = new DateTime(2026, 10, 2, 18, 0, 0, DateTimeKind.Utc)
        });

        cut.Find(".post-meta__when").TextContent.Should().Be("Published on 2 October 2026 · 7 min read");
    }

    [Fact]
    public void PostHero_TitleIsH1WithHeroClass()
    {
        var cut = RenderHero(Post());

        var h1 = cut.Find("h1");
        h1.ClassList.Should().Contain("hero-title");
        h1.TextContent.Should().Be("Rewriting my first Blazor app");
    }

    [Fact]
    public void PostHero_NoSubtitle_NoEmptyElement()
    {
        var cut = RenderHero(Post(subtitle: null));

        cut.Markup.Should().NotContain("post-hero__subtitle", "no empty subtitle element (Review Focus 1)");
        cut.Markup.Should().NotContain("null");
    }

    [Fact]
    public void PostHero_NoCategory_NoChip()
    {
        var cut = RenderHero(Post(category: null));

        cut.Markup.Should().NotContain("category-chip", "a post with no category shows no chip (Review Focus 1)");
        cut.Markup.Should().NotContain("null");
    }

    [Fact]
    public void PostHero_BannerOverlay_IsTheHerosOwnNotBannerOrFallbacks()
    {
        // The hero's dark overlay is anchored to its text block (PostHero's own gradient);
        // BannerOrFallback's fixed-height overlay is left to the cards that use it.
        var cut = RenderHero(Post());

        cut.Find(".banner-or-fallback").ClassList.Should().NotContain("banner-or-fallback--overlay");
    }
}
