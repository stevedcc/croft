using Croft.Shared.Contracts;
using Croft.Web.Components;
using Croft.Web.Components.Admin;
using Bunit;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace Croft.Web.Tests.Components;

/// <summary>
/// The image area shared by every post surface (spec §7.3): the banner image, or the site's
/// fallback banner under --fallback-wash with the category icon in white (Folder when there is no
/// category). Review Focus 1: missing banner / missing alt text / missing category render cleanly.
/// </summary>
public class BannerOrFallbackTests : BlogBunitContext
{
    private static readonly CategoryDto Terrain =
        new(Guid.NewGuid(), "Travel", "travel", "Terrain", 3);

    [Fact]
    public void BannerOrFallback_WithBanner_RendersImgWithAlt()
    {
        var cut = Render<BannerOrFallback>(p => p
            .Add(x => x.Banner, new BannerRefDto("/media/sunset.jpg", "Sunset over the mountains")));

        var img = cut.Find("img");
        img.GetAttribute("src").Should().Be("/media/sunset.jpg");
        img.GetAttribute("alt").Should().Be("Sunset over the mountains");
    }

    [Fact]
    public void BannerOrFallback_BannerWithoutAltText_RendersImgWithEmptyAlt()
    {
        // Review Focus 1: no broken <img> — alt must still be present (empty = decorative).
        var cut = Render<BannerOrFallback>(p => p
            .Add(x => x.Banner, new BannerRefDto("/media/plain.jpg", null)));

        var img = cut.Find("img");
        img.HasAttribute("alt").Should().BeTrue("an <img> without an alt attribute is an accessibility violation");
        img.GetAttribute("alt").Should().BeEmpty();
    }

    [Fact]
    public void BannerOrFallback_ShowIconFalse_RendersThePlainFallback()
    {
        var cut = Render<BannerOrFallback>(p => p.Add(x => x.Banner, null).Add(x => x.ShowIcon, false));

        cut.Find(".banner-or-fallback__fallback");
        cut.FindAll(".banner-or-fallback__icon").Should().BeEmpty();
    }

    [Fact]
    public void BannerOrFallback_NoBanner_ShowsTheSitesFallbackBannerWithCategoryIcon()
    {
        var cut = Render<BannerOrFallback>(p => p
            .Add(x => x.Banner, null)
            .Add(x => x.Category, Terrain)
            .Add(x => x.IconSize, 64d));

        var fallback = cut.Find(".banner-or-fallback__fallback");
        fallback.HasAttribute("style").Should().BeFalse(
            "the fallback banner URL lives in theme.css as --fallback-banner; the public policy bans style attributes (style-src-attr 'none')");
        cut.Markup.Should().NotContain("style=\"", "no element of this component may write a style attribute");
        var icon = cut.FindComponent<CategoryIcon>();
        icon.Instance.Name.Should().Be("Terrain");
        icon.Instance.Size.Should().Be(64);
    }

    [Fact]
    public void BannerOrFallback_FallbackBannerUrl_IsTheThemeCssToken()
    {
        // The site's fallback banner URL (with its content-hash cache-buster) is painted from
        // theme.css's --fallback-banner token — never from markup, which would need a style
        // attribute the public pages may not carry.
        var css = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Croft.Web", "Components", "Blog", "BannerOrFallback.razor.css"));

        css.Should().MatchRegex(@"background-image:\s*var\(--fallback-banner\)\s*;",
            "the fallback paints the banner from the theme.css token");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Croft.slnx")))
        {
            dir = dir.Parent!;
        }

        dir.Should().NotBeNull("the tests run from inside the repository");
        return dir!.FullName;
    }

    [Fact]
    public void BannerOrFallback_NoBannerNoCategory_RendersFolderIcon()
    {
        var cut = Render<BannerOrFallback>(p => p
            .Add(x => x.Banner, null)
            .Add(x => x.Category, null));

        cut.Find(".banner-or-fallback__fallback");
        cut.Markup.Should().Contain(Icons.Material.Filled.Folder);
    }

    [Fact]
    public void BannerOrFallback_Overlay_AddsTheGradientClass()
    {
        var without = Render<BannerOrFallback>(p => p.Add(x => x.Overlay, false));
        var with = Render<BannerOrFallback>(p => p.Add(x => x.Overlay, true));

        without.Find(".banner-or-fallback").ClassList.Should().NotContain("banner-or-fallback--overlay");
        with.Find(".banner-or-fallback").ClassList.Should().Contain("banner-or-fallback--overlay");
    }
}
