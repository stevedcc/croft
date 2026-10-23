using Croft.Shared.Contracts;
using Croft.Web.Components.Layout;
using Bunit;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests.Components;

/// <summary>Brief 10f item 2: the footer's pages, the "Running on Croft" line and its absence.</summary>
public class SiteFooterTests : BlogBunitContext
{
    private void UseBranding(bool showPoweredBy = true, string poweredByUrl = "") =>
        Services.AddSingleton(new Croft.Web.Branding
        {
            Name = "Test Blog",
            ShowPoweredBy = showPoweredBy,
            PoweredByUrl = poweredByUrl,
        });

    private static IReadOnlyList<PageNavItemDto> TwoPages() => [new("Impressum", "impressum"), new("Privacy", "privacy-policy")];

    [Fact]
    public void FooterPages_AreLinks_InTheGivenOrder_ThenTheVersionLine()
    {
        UseBranding(poweredByUrl: "https://example.com/croft");

        var cut = Render<SiteFooter>(p => p.Add(x => x.Pages, TwoPages()));

        var footer = cut.Find("footer");
        footer.GetAttribute("role").Should().Be("contentinfo");
        cut.FindAll("footer a").Select(a => (a.GetAttribute("href"), a.TextContent))
            .Should().Equal(("/impressum", "Impressum"), ("/privacy-policy", "Privacy"), ("https://example.com/croft", "Croft"));
        System.Text.RegularExpressions.Regex.Replace(footer.TextContent, @"\s+", " ").Trim().Should().Be(
            "Impressum · Privacy · Running on Croft v" + AppVersion.Current);
    }

    [Fact]
    public void WithoutAPoweredByUrl_CroftIsPlainText()
    {
        UseBranding();

        var cut = Render<SiteFooter>();

        cut.FindAll("footer a").Should().BeEmpty("with no pages and no URL there is no link");
        System.Text.RegularExpressions.Regex.Replace(cut.Find("footer").TextContent, @"\s+", " ").Trim()
            .Should().Be("Running on Croft v" + AppVersion.Current, "whitespace collapses as a browser collapses it");
    }

    [Fact]
    public void NoFooterPages_AndPoweredByOff_RendersNoFooterElementAtAll()
    {
        UseBranding(showPoweredBy: false);

        var cut = Render<SiteFooter>();

        cut.FindAll("footer").Should().BeEmpty();
        cut.Markup.Trim().Should().BeEmpty("nothing is rendered, not an empty wrapper");
    }

    [Fact]
    public void FooterPages_WithPoweredByOff_ShowsOnlyThePages()
    {
        UseBranding(showPoweredBy: false);

        var cut = Render<SiteFooter>(p => p.Add(x => x.Pages, TwoPages()));

        cut.Find("footer").TextContent.Should().NotContain("Running on");
        cut.FindAll("footer a").Should().HaveCount(2);
    }

    [Fact]
    public void TheFooter_CarriesNoInlineStyleOrScript()
    {
        UseBranding(poweredByUrl: "https://example.com/croft");

        var cut = Render<SiteFooter>(p => p.Add(x => x.Pages, TwoPages()));

        cut.FindAll("[style], script, [onclick]").Should().BeEmpty("the footer must be CSP-clean");
    }

    /// <summary>Brief 11a item 5: the profiles are icon links and the footer's first group, in
    /// the owner's order, each with its exact href, <c>rel="me noopener"</c>, a service-name
    /// aria-label and title, and an aria-hidden inline SVG filled with currentColor (the
    /// footer's link colour).</summary>
    [Fact]
    public void Profiles_AreIconLinks_First_InOwnerOrder_WithAriaLabelsAndMeNoopener()
    {
        Services.AddSingleton(BrandingWithProfiles(poweredByUrl: "https://example.com/croft"));

        var cut = Render<SiteFooter>(p => p.Add(x => x.Pages, TwoPages()));

        var profileAnchors = cut.FindAll("footer a").Take(4).ToList();
        string[] expectedHrefs =
        [
            "https://example.social/@someone",
            "https://bsky.app/profile/example.com",
            "https://github.com/someone",
            "https://www.linkedin.com/in/someone",
        ];
        profileAnchors.Select(a => a.GetAttribute("href")).Should().Equal(
            expectedHrefs,
            "the profiles are the first group, in the owner's order, with their exact URLs");
        string[] expectedLabels = ["Mastodon", "Bluesky", "GitHub", "LinkedIn"];
        profileAnchors.Select(a => a.GetAttribute("aria-label")).Should().Equal(
            expectedLabels,
            "each icon names its service for screen readers");
        foreach (var anchor in profileAnchors)
        {
            anchor.GetAttribute("rel").Should().Be("me noopener");
            anchor.GetAttribute("title").Should().Be(anchor.GetAttribute("aria-label"),
                "the tooltip carries the same service name");
            var svg = anchor.QuerySelector("svg");
            svg.Should().NotBeNull("each profile link holds its icon");
            svg!.GetAttribute("aria-hidden").Should().Be("true");
            svg.GetAttribute("focusable").Should().Be("false");
            svg.GetAttribute("fill").Should().Be("currentColor",
                "the icon takes the footer's link colour, not a hard-coded one");
        }
    }

    /// <summary>Brief 11a item 5: the icon group comes before the footer pages with the
    /// existing separator between groups: [icons] · Privacy Policy · Impressum · Running on
    /// Croft vX.Y.Z.</summary>
    [Fact]
    public void Profiles_Group_SitsBeforeThePages_WithTheSeparatorBetweenGroups()
    {
        Services.AddSingleton(BrandingWithProfiles(poweredByUrl: "https://example.com/croft"));

        var cut = Render<SiteFooter>(p => p.Add(x => x.Pages, TwoPages()));

        System.Text.RegularExpressions.Regex.Replace(cut.Find("footer").TextContent, @"\s+", " ").Trim()
            .Should().Be(
                "· Impressum · Privacy · Running on Croft v" + AppVersion.Current,
                "the icons carry no text; one separator divides each group");
    }

    /// <summary>Profiles alone are content: the footer renders (and nothing else).</summary>
    [Fact]
    public void OnlyProfiles_RenderTheFooterWithJustTheIcons()
    {
        Services.AddSingleton(BrandingWithProfiles(showPoweredBy: false));

        var cut = Render<SiteFooter>();

        cut.FindAll("footer a").Should().HaveCount(4, "four icon links and nothing else");
        cut.Find("footer").TextContent.Should().NotContain("Running on");
    }

    /// <summary>Brief 11a item 5: no style attribute anywhere in the footer, with or without
    /// profiles.</summary>
    [Fact]
    public void WithProfiles_TheFooter_CarriesNoInlineStyleOrScript()
    {
        Services.AddSingleton(BrandingWithProfiles(poweredByUrl: "https://example.com/croft"));

        var cut = Render<SiteFooter>(p => p.Add(x => x.Pages, TwoPages()));

        cut.FindAll("[style], script, [onclick]").Should().BeEmpty("the footer must be CSP-clean");
    }

    /// <summary>Brief 11a item 5: with no profiles the footer markup is exactly what it is
    /// today — the pages and the version line, nothing else.</summary>
    [Fact]
    public void WithoutProfiles_TheFooter_MarkupIsExactlyThePagesAndVersionLine()
    {
        UseBranding(poweredByUrl: "https://example.com/croft");

        var cut = Render<SiteFooter>(p => p.Add(x => x.Pages, TwoPages()));

        NormalizeMarkup(cut.Markup).Should().Be(
            NormalizeMarkup(
                """
                <footer class="site-footer" role="contentinfo"><a href="/impressum">Impressum</a> <span class="site-footer__sep" aria-hidden="true">·</span> <a href="/privacy-policy">Privacy</a> <span class="site-footer__sep" aria-hidden="true">·</span> <span class="site-footer__powered">Running on <a href="https://example.com/croft">Croft</a> v{version}</span></footer>
                """).Replace("{version}", AppVersion.Current));
    }

    private static Croft.Web.Branding BrandingWithProfiles(bool showPoweredBy = true, string poweredByUrl = "") =>
        new()
        {
            Name = "Test Blog",
            ShowPoweredBy = showPoweredBy,
            PoweredByUrl = poweredByUrl,
            Profiles =
            [
                new("mastodon", "https://example.social/@someone"),
                new("bluesky", "https://bsky.app/profile/example.com"),
                new("github", "https://github.com/someone"),
                new("linkedin", "https://www.linkedin.com/in/someone"),
            ],
        };

    /// <summary>Markup compare modulo what bUnit/Blazor add beyond the author's own markup:
    /// CSS-isolation scope attributes, boundary comments, whitespace (a non-breaking space is a
    /// space here) — so the assertion pins the footer's own structure and text.</summary>
    private static string NormalizeMarkup(string markup) =>
        System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(
                markup.Replace("\u00A0", " ").Replace("<!--!-->", string.Empty),
                @"\sb-[a-z0-9]+", string.Empty),
            @"\s+", " ").Trim();
}
