using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Croft.Web.Components;
using Croft.Web.Components.Admin;
using Bunit;
using AwesomeAssertions;
using MudBlazor;

namespace Croft.Web.Tests.Components;

/// <summary>
/// Task R3.2: PostCard (spec §5.1) — whole card is one &lt;a&gt; whose accessible name is the title;
/// Featured = image area with overlaid chip/title/subtitle, List = image left at 34%. Review Focus
/// 1 (missing optional data) and 2 (long text) are pinned here.
/// </summary>
public class PostCardTests : BlogBunitContext
{
    private static readonly CategoryDto Terrain =
        new(Guid.NewGuid(), "Travel", "travel", "Terrain", 3);

    private static PostSummaryDto Post(
        string title = "Rewriting my first Blazor app",
        string? subtitle = "What three years of technical debt taught me about components",
        string summary = "When I opened the solution again after three years, the first thing I noticed was how much state lived in places it had no business being.",
        CategoryDto? category = null,
        BannerRefDto? banner = null) =>
        new(Guid.NewGuid(), "rewriting-my-first-blazor-app", title, subtitle, null, summary, 7,
            PostStatus.Published, new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc),
            "An Author", category, [], banner, null, null);

    private IRenderedComponent<PostCard> RenderCard(PostSummaryDto post, PostCardVariant variant) =>
        Render<PostCard>(p => p.Add(x => x.Post, post).Add(x => x.Variant, variant));

    [Theory]
    [InlineData(PostCardVariant.Featured, false)]
    [InlineData(PostCardVariant.List, true)]
    public void PostCard_WithSocialCounts_ShowsThePillInTheMedia_CompactOnTheListCard(PostCardVariant variant, bool compact)
    {
        var post = Post() with { Social = new Croft.Shared.Contracts.Social.SocialCountsDto(12, 5, 5) };

        var cut = RenderCard(post, variant);

        var pill = cut.Find(".post-card__link .post-card__media .social-counts");
        pill.GetAttribute("aria-label").Should().Be("12 likes, 5 reposts, 5 replies");
        pill.ClassList.Contains("social-counts--compact").Should().Be(compact);
        cut.Find("article").ClassList.Should().Contain("post-card--counts", "the featured fallback icon makes room for the pill");
    }

    [Theory]
    [InlineData(PostCardVariant.Featured)]
    [InlineData(PostCardVariant.List)]
    public void PostCard_WithoutSocialCounts_ShowsNoPill(PostCardVariant variant) =>
        RenderCard(Post(), variant).FindAll(".social-counts").Should().BeEmpty();

    [Fact]
    public void PostCard_Featured_WithBanner_RendersImgWithAlt()
    {
        var post = Post(banner: new BannerRefDto("/media/sunset.jpg", "Sunset over the mountains"));

        var cut = RenderCard(post, PostCardVariant.Featured);

        var img = cut.Find("img");
        img.GetAttribute("src").Should().Be("/media/sunset.jpg");
        img.GetAttribute("alt").Should().Be("Sunset over the mountains");
    }

    [Fact]
    public void PostCard_List_WithBanner_RendersImgWithAlt()
    {
        var post = Post(banner: new BannerRefDto("/media/sunset.jpg", "Sunset over the mountains"));

        var cut = RenderCard(post, PostCardVariant.List);

        var img = cut.Find("img");
        img.GetAttribute("src").Should().Be("/media/sunset.jpg");
        img.GetAttribute("alt").Should().Be("Sunset over the mountains");
    }

    [Fact]
    public void PostCard_NoBanner_RendersFallbackAndCategoryIcon()
    {
        var cut = RenderCard(Post(category: Terrain), PostCardVariant.Featured);

        cut.Find(".banner-or-fallback__fallback");
        var icon = cut.FindComponent<CategoryIcon>();
        icon.Instance.Name.Should().Be("Terrain");
        icon.Instance.Size.Should().Be(64, "the featured fallback icon is 64px (plan key rules)");
    }

    [Fact]
    public void PostCard_List_NoBanner_ShowsThePlainTartanWithNoIcon()
    {
        var cut = RenderCard(Post(category: Terrain), PostCardVariant.List);

        cut.Find(".banner-or-fallback__fallback");
        cut.FindAll(".banner-or-fallback__icon").Should().BeEmpty(
            "the list card's category chip already shows the icon and name (brief 10b item 6)");
    }

    [Fact]
    public void PostCard_NoCategory_NoChip_FolderIconInFallback()
    {
        var cut = RenderCard(Post(category: null), PostCardVariant.Featured);

        cut.FindAll(".category-chip").Should().BeEmpty("a post with no category must show no chip");
        cut.FindAll(".post-card__chip").Should().BeEmpty("no empty chip slot either");
        cut.Markup.Should().NotContain("category-chip");
        cut.Markup.Should().Contain(Icons.Material.Filled.Folder, "the fallback without a category is the Folder icon");
    }

    [Theory]
    [InlineData(PostCardVariant.Featured)]
    [InlineData(PostCardVariant.List)]
    public void PostCard_NoSubtitle_NoEmptyElement(PostCardVariant variant)
    {
        var cut = RenderCard(Post(subtitle: null), variant);

        cut.FindAll(".post-card__subtitle").Should().BeEmpty();
        cut.Markup.Should().NotContain("post-card__subtitle");
    }

    [Theory]
    [InlineData(PostCardVariant.Featured)]
    [InlineData(PostCardVariant.List)]
    public void PostCard_EmptySummary_NoEmptyParagraph(PostCardVariant variant)
    {
        var cut = RenderCard(Post(summary: ""), variant);

        cut.FindAll(".post-card__summary").Should().BeEmpty();
        cut.Markup.Should().NotContain("post-card__summary");
    }

    [Theory]
    [InlineData(PostCardVariant.Featured)]
    [InlineData(PostCardVariant.List)]
    public void PostCard_LinkNameComesFromItsContent_AndContainsTheTitle(PostCardVariant variant)
    {
        // Brief 13g item 5 (review M-14): the link no longer carries an aria-label (it replaced the
        // summary and date in the name and broke Label in Name if the h3 ever differed); its name is
        // its content, which starts with the title the reader sees.
        var post = Post(title: "Notes on nullable reference types");

        var cut = RenderCard(post, variant);

        var link = cut.Find("a.post-card__link");
        link.GetAttribute("href").Should().Be($"/post/{post.Slug}");
        link.HasAttribute("aria-label").Should().BeFalse();
        AccessibleName(link).Should().Contain(post.Title, "the card link's accessible name includes the title");
    }

    [Fact]
    public void PostCard_WholeCardIsOneLink()
    {
        var post = Post(category: Terrain);
        var cut = RenderCard(post, PostCardVariant.List);

        cut.FindAll($"a[href='/post/{post.Slug}']").Should().HaveCount(1, "the whole card is one link, not several");
        var link = cut.Find("a.post-card__link");
        link.QuerySelector(".post-card__title").Should().NotBeNull();
        link.QuerySelector(".post-card__summary").Should().NotBeNull();
        link.QuerySelector(".post-card__footer").Should().NotBeNull();
    }

    [Fact]
    public void PostCard_MissingOptionalFields_NoNullText_NoEmptyBits()
    {
        // Review Focus 1: no category, no banner, no subtitle, empty summary, no tags — and the
        // word "null" must never reach the markup.
        var post = Post(subtitle: null, summary: "", category: null);
        var cut = RenderCard(post, PostCardVariant.Featured);

        cut.Markup.Should().NotContain("null");
        cut.FindAll(".category-chip").Should().BeEmpty();
        cut.FindAll(".post-card__chip").Should().BeEmpty();
        cut.FindAll(".post-card__subtitle").Should().BeEmpty();
        cut.FindAll(".post-card__summary").Should().BeEmpty();
    }

    [Theory]
    [InlineData(PostCardVariant.Featured)]
    [InlineData(PostCardVariant.List)]
    public void PostCard_LongText_RendersIntact(PostCardVariant variant)
    {
        // Review Focus 2: 300-char title/subtitle render without being cut in the DOM (clamping is
        // CSS, pinned in CardCssContractTests; the 390px overflow check is Task R3.6's E2E).
        var title = new string('T', 300);
        var subtitle = new string('S', 300);
        var summary = new string('W', 300);

        var cut = RenderCard(Post(title: title, subtitle: subtitle, summary: summary), variant);

        cut.Find(".post-card__title").TextContent.Should().Be(title);
        cut.Find(".post-card__subtitle").TextContent.Should().Be(subtitle);
        cut.Find(".post-card__summary").TextContent.Should().Be(summary);
    }

    [Fact]
    public void PostCard_Featured_UsesOverlayTextLayout_ListDoesNot()
    {
        var featured = RenderCard(Post(), PostCardVariant.Featured);
        var list = RenderCard(Post(), PostCardVariant.List);

        featured.Find(".post-card").ClassList.Should().Contain("post-card--featured");
        featured.FindAll(".post-card__overlay").Should().HaveCount(1, "the featured title sits over the image");
        list.Find(".post-card").ClassList.Should().Contain("post-card--list");
        list.FindAll(".post-card__overlay").Should().BeEmpty("the list card keeps its text beside the image");
    }

    /// <summary>The link's accessible name: aria-label when present, else its text content (W3C name computation, simplified).</summary>
    private static string? AccessibleName(AngleSharp.Dom.IElement link) =>
        link.GetAttribute("aria-label") ?? link.TextContent?.Trim();
}
