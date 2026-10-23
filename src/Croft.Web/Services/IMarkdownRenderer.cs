namespace Croft.Web.Services;

public sealed record Heading(int Level, string Text, string Id);

public sealed record RenderedMarkdown(string Html, IReadOnlyList<Heading> Headings);

/// <summary>What a render adds beyond the public pages' output.</summary>
public sealed record MarkdownRenderOptions
{
    /// <summary>
    /// Adds <c>data-source-line="n"</c> (the block's 1-based line in the Markdown source) to every
    /// top-level block, so the editor's Preview can follow the Markdown box's scroll position. Only
    /// the editor's Preview sets this; the public pages never do, and their HTML is unchanged.
    /// </summary>
    public bool SourceLines { get; init; }
}

public interface IMarkdownRenderer
{
    RenderedMarkdown Render(string markdown);

    RenderedMarkdown Render(string markdown, MarkdownRenderOptions options);
}
