using Croft.Web.Services;
using AwesomeAssertions;
using Markdig;

namespace Croft.Web.Tests;

public class MarkdownRendererTests
{
    [Fact]
    public void Render_RendersMarkdown()
    {
        var renderer = new MarkdownRenderer();
        var html = renderer.Render("# Hello").Html;
        html.Should().Contain("<h1").And.Contain(">Hello</h1>");
    }

    [Theory]
    [InlineData("<script>alert('xss')</script>", "script")]
    [InlineData("<img src=x onerror=alert(1)>", "img")]
    [InlineData("<iframe src=\"javascript:alert(1)\"></iframe>", "iframe")]
    [InlineData("<a href=\"javascript:alert(1)\">click</a>", "a")]
    [InlineData("<svg onload=alert(1)>", "svg")]
    public void Render_RawHtmlStillDisabled_TheTagComesOutEscapedAsText(string markdown, string tag)
    {
        var html = new MarkdownRenderer().Render(markdown).Html;

        // "No <script> in the output" alone is also true of a renderer that silently dropped the input;
        // the contract is that raw HTML is not HTML: it is shown as the escaped text it was written as.
        html.Should().Contain($"&lt;{tag}", "the tag is rendered as text");
        html.Should().NotContain($"<{tag}", "no such element reaches the page");
        System.Text.RegularExpressions.Regex.IsMatch(html, @"<[^>]*\bon\w+\s*=").Should().BeFalse(
            "no element carries an event-handler attribute");
    }

    [Fact]
    public void Render_ReturnsH2AndH3Only()
    {
        var renderer = new MarkdownRenderer();
        var markdown = "# One\n\n## Two\n\n### Three\n\n#### Four\n";
        var result = renderer.Render(markdown);
        result.Headings.Should().HaveCount(2);
        result.Headings[0].Should().Be(new Heading(2, "Two", "two"));
        result.Headings[1].Should().Be(new Heading(3, "Three", "three"));
    }

    [Fact]
    public void Render_DuplicateHeadings_GetUniqueIds()
    {
        var renderer = new MarkdownRenderer();
        var result = renderer.Render("## Foo\n\n## Foo\n\n## Foo\n");
        result.Headings.Select(heading => heading.Id).Should().Equal("foo", "foo-1", "foo-2");
        result.Headings.Select(heading => heading.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Render_HeadingWithCodeAndLink_TextIsPlain()
    {
        var renderer = new MarkdownRenderer();
        var result = renderer.Render("## Use `code` and [link](https://example.com)\n");
        result.Headings.Should().ContainSingle().Which.Text.Should().Be("Use code and link");
    }

    [Fact]
    public void Render_NoHeadings_EmptyList()
    {
        var renderer = new MarkdownRenderer();
        var result = renderer.Render("Just a paragraph.\n");
        result.Headings.Should().BeEmpty();
        result.Html.Should().Contain("Just a paragraph.");
    }

    private const string MixedDocument = """
        # Title

        First paragraph with *emphasis*.

        - item one
        - item two

          nested paragraph

        ```csharp
        var x = 1;
        ```

        > a quote

        ## Section

        Last paragraph.
        """;

    [Fact]
    public void Render_PublicRender_HasNoSourceLineAttribute()
    {
        new MarkdownRenderer().Render(MixedDocument).Html.Should().NotContain("data-source-line");
    }

    [Fact]
    public void Render_WithoutTheSourceLineOption_IsByteIdenticalToThePublicRender()
    {
        var renderer = new MarkdownRenderer();

        renderer.Render(MixedDocument, new MarkdownRenderOptions()).Should().BeEquivalentTo(renderer.Render(MixedDocument));
    }

    [Fact]
    public void Render_PreviewOption_MarksEveryTopLevelBlockWithItsOneBasedSourceLine()
    {
        var html = new MarkdownRenderer().Render(MixedDocument, new MarkdownRenderOptions { SourceLines = true }).Html;

        var lines = System.Text.RegularExpressions.Regex.Matches(html, "data-source-line=\"(\\d+)\"")
            .Select(match => int.Parse(match.Groups[1].Value)).ToList();
        // Title, paragraph, list, code fence, quote, section heading, last paragraph.
        lines.Should().Equal(1, 3, 5, 10, 14, 16, 18);
    }

    [Fact]
    public void Render_PreviewOption_ChangesNothingButTheAttribute()
    {
        var renderer = new MarkdownRenderer();

        var preview = renderer.Render(MixedDocument, new MarkdownRenderOptions { SourceLines = true });

        System.Text.RegularExpressions.Regex.Replace(preview.Html, " data-source-line=\"\\d+\"", string.Empty)
            .Should().Be(renderer.Render(MixedDocument).Html, "the public output must stay byte for byte what it was");
        preview.Headings.Should().Equal(renderer.Render(MixedDocument).Headings);
    }

    // Brief 13a item 5: author-supplied attributes and dangerous URLs never reach the page.

    [Theory]
    [InlineData("![pic](/api/media/0f8f){onerror=alert(1)}", "onerror")]
    [InlineData("Normal paragraph{style=\"position:fixed;inset:0\"}", "style=")]
    [InlineData("Text {.class #id}", "class=")]
    [InlineData("Text {.class #id}", "id=\"id\"")]
    [InlineData("## Heading {#custom-id .big}", "id=\"custom-id\"")]
    [InlineData("```csharp {onclick=x}\nvar a = 1;\n```", "onclick")]
    public void Render_AuthorAttributes_AreNeverEmitted(string markdown, string forbidden)
    {
        var html = new MarkdownRenderer().Render(markdown).Html;

        // The braces render as (escaped) text, so only the tags are searched.
        var tags = string.Concat(System.Text.RegularExpressions.Regex.Matches(html, "<[^>]*>").Select(match => match.Value));
        tags.Should().NotContain(forbidden, "GenericAttributes is off: braces are text, not an attribute");
    }

    [Fact]
    public void Render_BracesAfterAnImage_RenderAsTextAfterTheImage()
    {
        // Pinned: with GenericAttributes off Markdig leaves the braces as ordinary text.
        var html = new MarkdownRenderer().Render("![pic](/a.png){onerror=alert(1)}").Html;

        html.Should().Be("<p><img src=\"/a.png\" alt=\"pic\" />{onerror=alert(1)}</p>\n");
    }

    [Theory]
    [InlineData("[x](javascript:alert(1))", "x")]
    [InlineData("[x](JaVaScRiPt:alert(1))", "x")]
    [InlineData("[x]( javascript:alert(1))", "x")]
    [InlineData("[x](java\tscript:alert(1))", "x")]
    [InlineData("[x](vbscript:msgbox(1))", "x")]
    [InlineData("[x](file:///etc/passwd)", "x")]
    [InlineData("[x](data:text/html,<b>x</b>)", "x")]
    [InlineData("[x][ref]\n\n[ref]: javascript:alert(1)", "x")]
    [InlineData("<javascript:alert(1)>", "javascript:alert(1)")]
    public void Render_LinksWithADangerousScheme_RenderAsPlainText(string markdown, string text)
    {
        var html = new MarkdownRenderer().Render(markdown).Html;

        html.Should().NotContain("<a ").And.NotContain("href=");
        html.Should().Contain(text);
    }

    [Theory]
    [InlineData("![x](data:image/svg+xml,abc)")]
    [InlineData("![x](javascript:alert(1))")]
    [InlineData("![x](JAVASCRIPT:alert(1))")]
    public void Render_ImagesWithADangerousScheme_RenderTheAltTextAndNoImg(string markdown)
    {
        var html = new MarkdownRenderer().Render(markdown).Html;

        html.Should().NotContain("<img").And.NotContain("src=").And.NotContain("data:image");
        html.Should().Be("<p>x</p>\n");
    }

    [Theory]
    [InlineData("[x](http://example.com/a)", "href=\"http://example.com/a\"")]
    [InlineData("[x](https://example.com/a?b=1&c=2)", "href=\"https://example.com/a?b=1&amp;c=2\"")]
    [InlineData("[x](mailto:me@example.com)", "href=\"mailto:me@example.com\"")]
    [InlineData("[x](/relative/path)", "href=\"/relative/path\"")]
    [InlineData("[x](relative/path:with-colon)", "href=\"relative/path:with-colon\"")]
    [InlineData("[x](#fragment)", "href=\"#fragment\"")]
    [InlineData("[x](?q=a:b)", "href=\"?q=a:b\"")]
    [InlineData("<https://example.com>", "href=\"https://example.com\"")]
    [InlineData("<me@example.com>", "href=\"mailto:me@example.com\"")]
    [InlineData("Visit https://example.com/page now", "href=\"https://example.com/page\"")]
    [InlineData("![alt](/api/media/abc)", "src=\"/api/media/abc\"")]
    [InlineData("![alt](https://example.com/p.png)", "src=\"https://example.com/p.png\"")]
    public void Render_SafeLinksAndImages_StillRender(string markdown, string expected)
    {
        new MarkdownRenderer().Render(markdown).Html.Should().Contain(expected);
    }

    [Fact]
    public void Render_Tables_StillWork()
    {
        var html = new MarkdownRenderer().Render("| a | b |\n|---|---|\n| 1 | 2 |\n").Html;
        html.Should().Contain("<table>").And.Contain("<th>a</th>").And.Contain("<td>2</td>");
    }

    [Fact]
    public void Render_TaskLists_StillWork()
    {
        var html = new MarkdownRenderer().Render("- [x] done\n- [ ] todo\n").Html;
        html.Should().Contain("task-list-item").And.Contain("type=\"checkbox\"");
    }

    [Fact]
    public void Render_Footnotes_StillWork()
    {
        var html = new MarkdownRenderer().Render("Text[^1]\n\n[^1]: The note.\n").Html;
        html.Should().Contain("footnote").And.Contain("The note.");
    }

    [Fact]
    public void Render_EmphasisExtras_StillWork()
    {
        var html = new MarkdownRenderer().Render("~~gone~~ and H~2~O and ==marked==\n").Html;
        html.Should().Contain("<del>gone</del>").And.Contain("<sub>2</sub>").And.Contain("<mark>marked</mark>");
    }

    [Fact]
    public void Render_DefinitionLists_StillWork()
    {
        new MarkdownRenderer().Render("Term\n:   Definition\n").Html.Should().Contain("<dl>").And.Contain("<dd>Definition</dd>");
    }

    [Fact]
    public void Render_Abbreviations_StillWork()
    {
        new MarkdownRenderer().Render("The HTML spec\n\n*[HTML]: Hyper Text Markup Language\n").Html
            .Should().Contain("<abbr title=\"Hyper Text Markup Language\">HTML</abbr>");
    }

    [Fact]
    public void Render_CustomContainers_StillWork()
    {
        new MarkdownRenderer().Render(":::\ninside\n:::\n").Html.Should().Contain("<div>").And.Contain("inside");
    }

    [Fact]
    public void Render_Mathematics_StillWork()
    {
        new MarkdownRenderer().Render("Inline $x^2$ math\n").Html.Should().Contain("math").And.Contain("x^2");
    }

    [Fact]
    public void Render_AlertBlocks_StillWork()
    {
        new MarkdownRenderer().Render("> [!NOTE]\n> Careful\n").Html.Should().Contain("markdown-alert").And.Contain("Careful");
    }

    [Fact]
    public void Render_ListExtras_StillWork()
    {
        new MarkdownRenderer().Render("a. one\nb. two\n").Html.Should().Contain("<ol type=\"a\">");
    }

    [Fact]
    public void Render_AutoIdentifiers_StillWork()
    {
        new MarkdownRenderer().Render("## Some Heading\n").Html.Should().Contain("id=\"some-heading\"");
    }

    [Fact]
    public void Render_Figures_StillWork()
    {
        new MarkdownRenderer().Render("^^^\nfigure body\n^^^ caption\n").Html.Should().Contain("<figure>").And.Contain("<figcaption>");
    }

    [Fact]
    public void Render_Citations_StillWork()
    {
        new MarkdownRenderer().Render("a \"\"quoted\"\" b\n").Html.Should().Contain("<cite>quoted</cite>");
    }

    [Fact]
    public void Render_GridTables_StillWork()
    {
        var md = "+---+---+\n| a | b |\n+===+===+\n| 1 | 2 |\n+---+---+\n";
        new MarkdownRenderer().Render(md).Html.Should().Contain("<table>");
    }

    [Fact]
    public void TheSharedPipeline_IsExactlyTheAdvancedExtensionsMinusGenericAttributes()
    {
        var advanced = new MarkdownPipelineBuilder().UseAdvancedExtensions().Extensions
            .Select(extension => extension.GetType()).Where(type => type != typeof(Markdig.Extensions.GenericAttributes.GenericAttributesExtension));
        var ours = Croft.Shared.Markdown.CroftMarkdown.CreateBuilder().Extensions
            .Select(extension => extension.GetType()).Where(type => type != typeof(Croft.Shared.Markdown.SafeLinkExtension));

        ours.Should().Equal(advanced,
            "Markdig's advanced set changed: review the new extension (attributes? URLs?) before listing it in CroftMarkdown");
        Croft.Shared.Markdown.CroftMarkdown.CreateBuilder().Extensions
            .Should().NotContain(extension => extension is Markdig.Extensions.GenericAttributes.GenericAttributesExtension);
    }
}
