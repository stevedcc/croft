using System.Text;
using System.Text.RegularExpressions;
using Croft.Shared;
using Croft.Shared.Markdown;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Croft.Api.Services;

/// <summary>
/// Derives a plain-text summary and a reading-time estimate from post markdown.
/// The summary is the first top-level paragraph (headings, image-only paragraphs and
/// code blocks are skipped) rendered without markup, whitespace-collapsed and truncated
/// at a word boundary. Reading time is ceil(words / 220), minimum 1, counting the plain
/// text of every non-code block.
/// </summary>
public static partial class PostSummaryExtractor
{
    private const int MaxSummaryLength = 300;

    // The same extension list as the Web renderer's (CroftMarkdown): this pipeline renders plain
    // text, but the two must parse alike.
    private static readonly MarkdownPipeline Pipeline = CroftMarkdown.CreateBuilder().Build();

    public static (string Summary, int ReadingMinutes) Extract(string markdown)
    {
        var document = Markdown.Parse(markdown, Pipeline);

        var summary = Truncate(BuildSummary(document));
        var words = CountWords(document);
        var readingMinutes = ReadingTime.FromWords(words);

        return (summary, readingMinutes);
    }

    private static string BuildSummary(MarkdownDocument document)
    {
        foreach (var block in document)
        {
            if (block is not ParagraphBlock paragraph || IsImageOnlyParagraph(paragraph))
                continue;

            return CollapseWhitespace(RenderInlines(paragraph.Inline));
        }

        return string.Empty;
    }

    private static bool IsImageOnlyParagraph(ParagraphBlock paragraph)
    {
        var hasImage = false;

        for (var inline = paragraph.Inline?.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LinkInline { IsImage: true }:
                    hasImage = true;
                    break;
                case LiteralInline literal when string.IsNullOrWhiteSpace(literal.Content.ToString()):
                    break;
                case LineBreakInline:
                    break;
                default:
                    return false;
            }
        }

        return hasImage;
    }

    private static string RenderInlines(ContainerInline? parent)
    {
        var sb = new StringBuilder();

        for (var inline = parent?.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    sb.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    sb.Append(code.Content);
                    break;
                case LineBreakInline:
                    sb.Append(' ');
                    break;
                case LinkInline link:
                    sb.Append(RenderInlines(link));
                    break;
                case EmphasisInline emphasis:
                    sb.Append(RenderInlines(emphasis));
                    break;
            }
        }

        return sb.ToString();
    }

    private static string CollapseWhitespace(string text) =>
        Regex.Replace(text, @"\s+", " ").Trim();

    private static string Truncate(string text)
    {
        if (text.Length <= MaxSummaryLength)
            return text;

        // Cut at the last space at or before 300 characters; hard-cut when there is none.
        var cut = text.LastIndexOf(' ', MaxSummaryLength - 1);
        if (cut <= 0)
            cut = MaxSummaryLength;

        // A cut that lands on the end of a sentence needs no ellipsis (".…" reads as a typo).
        var cutText = text[..cut].TrimEnd();
        return SentenceEnd().IsMatch(cutText) ? cutText : cutText + "…";
    }

    // Sentence-final punctuation, optionally followed by closing quotes or brackets.
    [GeneratedRegex("""[.!?]["'\u201D\u2019\u2018\u201E\u203A\u00AB\u00BB)\]}]*$""")]
    private static partial Regex SentenceEnd();

    private static int CountWords(MarkdownDocument document)
    {
        var sb = new StringBuilder();
        AppendBlockText(document, sb);
        return sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static void AppendBlockText(ContainerBlock container, StringBuilder sb)
    {
        foreach (var block in container)
        {
            switch (block)
            {
                case CodeBlock: // includes FencedCodeBlock; code contributes no words
                    break;
                case LeafBlock { Inline: { } inline }:
                    sb.Append(CollapseWhitespace(RenderInlines(inline)));
                    sb.Append(' ');
                    break;
                case ContainerBlock nested:
                    AppendBlockText(nested, sb);
                    break;
            }
        }
    }
}
