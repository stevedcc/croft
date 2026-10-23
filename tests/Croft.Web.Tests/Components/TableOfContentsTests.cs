using Croft.Web.Components;
using Croft.Web.Services;
using Bunit;
using AwesomeAssertions;

namespace Croft.Web.Tests.Components;

/// <summary>
/// Task R3.4: the "On this page" TOC (spec §5.4) — hidden below two headings, links to the
/// headings' Markdig auto-identifier anchors. Ruling R3-4-a: hrefs are path-qualified
/// ({Path}#{Id}); a bare "#id" resolves against &lt;base href="/"&gt; and would leave the post
/// page, so bare fragment hrefs are forbidden. Review Focus 3 (headings edge cases) is pinned
/// here: duplicate heading texts get unique ids that match the rendered HTML, and headings
/// containing code/links show plain text.
/// </summary>
public class TableOfContentsTests : BlogBunitContext
{
    private static Heading H2(string text, string id) => new(2, text, id);
    private static Heading H3(string text, string id) => new(3, text, id);

    private IRenderedComponent<TableOfContents> RenderToc(IReadOnlyList<Heading> headings, string path = "/post/demo") =>
        Render<TableOfContents>(p => p.Add(x => x.Headings, headings).Add(x => x.Path, path));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void TableOfContents_HiddenWhenFewerThan2Headings(int headingCount)
    {
        var headings = headingCount == 0
            ? []
            : new[] { H2("Only one", "only-one") };

        var cut = RenderToc(headings);

        cut.Markup.Should().BeEmpty("a TOC for fewer than two headings is noise (spec §5.4)");
    }

    [Fact]
    public void TableOfContents_LinksToIds()
    {
        var cut = RenderToc(
        [
            H2("Introduction", "introduction"),
            H3("Where the state was hiding", "where-the-state-was-hiding"),
        ]);

        var links = cut.FindAll("a");
        links.Should().HaveCount(2);
        links[0].GetAttribute("href").Should().Be("/post/demo#introduction");
        links[1].GetAttribute("href").Should().Be("/post/demo#where-the-state-was-hiding");
        links[0].TextContent.Should().Be("Introduction");

        // Ruling R3-4-a: bare "#id" hrefs resolve to "/#id" and navigate away — never render one.
        cut.Markup.Should().NotContain("href=\"#", "a bare fragment href would leave the post page");
    }

    [Fact]
    public void TableOfContents_DuplicateHeadingTexts_LinkToUniqueIdsInTheDocument()
    {
        // Review Focus 3: duplicate heading texts must get unique anchor ids — asserted on the
        // rendered HTML itself, and every TOC link must point at one of those ids.
        var rendered = new MarkdownRenderer().Render("## Foo\n\n## Foo\n");

        rendered.Html.Should().Contain("id=\"foo\"").And.Contain("id=\"foo-1\"",
            "Markdig auto-identifiers make duplicate headings unique");

        var cut = RenderToc(rendered.Headings);

        var hrefs = cut.FindAll("a").Select(a => a.GetAttribute("href")).ToList();
        hrefs.Should().Equal("/post/demo#foo", "/post/demo#foo-1");
        hrefs.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void TableOfContents_HeadingsWithCodeAndLinks_ShowPlainText()
    {
        // Review Focus 3: the TOC text is the heading's plain text — no markup leaks into it.
        // Two headings because the TOC hides itself below two (see the first test).
        var rendered = new MarkdownRenderer().Render("## Use `code` and [link](https://example.com)\n\n## Second\n");

        var cut = RenderToc(rendered.Headings);

        var link = cut.FindAll("a")[0];
        link.TextContent.Should().Be("Use code and link");
        cut.Markup.Should().NotContain("<code").And.NotContain("<a href=\"https://example.com\"");
    }
}
