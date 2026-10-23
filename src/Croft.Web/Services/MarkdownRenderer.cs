using System.Globalization;
using Croft.Shared.Markdown;
using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace Croft.Web.Services;

public class MarkdownRenderer : IMarkdownRenderer
{
    private readonly MarkdownPipeline _pipeline;

    public MarkdownRenderer()
    {
        // CroftMarkdown: the advanced extensions written out, minus GenericAttributes (author
        // attributes), with links and images limited to safe URLs. Raw HTML stays disabled.
        _pipeline = CroftMarkdown.CreateBuilder()
            .DisableHtml()
            .Build();
    }

    public RenderedMarkdown Render(string markdown) => Render(markdown, new MarkdownRenderOptions());

    public RenderedMarkdown Render(string markdown, MarkdownRenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var document = Markdown.Parse(markdown, _pipeline);
        if (options.SourceLines)
        {
            foreach (var block in document)
            {
                // Markdig's Line is 0-based; the editor numbers its lines from 1.
                block.GetAttributes().AddProperty("data-source-line", (block.Line + 1).ToString(CultureInfo.InvariantCulture));
            }
        }

        var headings = document
            .Descendants<HeadingBlock>()
            .Where(heading => heading.Level is 2 or 3)
            .Select(heading => new Heading(
                heading.Level,
                GetPlainText(heading),
                heading.GetAttributes().Id ?? string.Empty))
            .ToList();

        return new RenderedMarkdown(document.ToHtml(_pipeline), headings);
    }

    // WORKAROUND(W-051, Markdig): a second renderer without inline HTML yields the tag-free text AutoIdentifiers uses for heading ids
    /// <summary>
    /// Plain inline text of a heading (code and link text included, no markup). Uses the same
    /// tag-free rendering Markdig's AutoIdentifiers uses to build the heading id.
    /// </summary>
    private static string GetPlainText(HeadingBlock heading)
    {
        if (heading.Inline is null)
        {
            return string.Empty;
        }

        var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer)
        {
            EnableHtmlForInline = false,
            EnableHtmlEscape = false,
        };
        renderer.Render(heading.Inline);
        return writer.ToString();
    }
    // END WORKAROUND(W-051)
}
