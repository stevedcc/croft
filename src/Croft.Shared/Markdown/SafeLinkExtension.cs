using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Renderers.Html.Inlines;
using Markdig.Syntax.Inlines;

namespace Croft.Shared.Markdown;

/// <summary>
/// Links and images whose URL has a scheme other than http, https or mailto (javascript:, data:,
/// vbscript:, file:, ...) render as plain text: the link's text without an anchor, the image's alt
/// text without an img. Relative URLs and #fragments are not schemes and pass. Covers inline and
/// reference links and images, autolinks (<c>&lt;javascript:...&gt;</c>) and bare URLs.
/// </summary>
public sealed class SafeLinkExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        if (renderer is not HtmlRenderer html)
        {
            return;
        }

        Replace<LinkInlineRenderer>(html, inner => new SafeLinkInlineRenderer(inner));
        Replace<AutolinkInlineRenderer>(html, inner => new SafeAutolinkInlineRenderer(inner));
    }

    private static void Replace<TRenderer>(HtmlRenderer renderer, Func<TRenderer, IMarkdownObjectRenderer> create)
        where TRenderer : class, IMarkdownObjectRenderer
    {
        var existing = renderer.ObjectRenderers.FindExact<TRenderer>();
        if (existing is null)
        {
            return;
        }

        renderer.ObjectRenderers.Replace<TRenderer>(create(existing));
    }

    /// <summary>
    /// Whether a link destination may become an href or src. Browsers drop ASCII tab and newline
    /// anywhere in a URL and ignore leading control characters and spaces, so every control
    /// character and whitespace is removed before the scheme is read.
    /// </summary>
    public static bool IsSafeUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return true;
        }

        var cleaned = new string([.. url.Where(c => !char.IsControl(c) && !char.IsWhiteSpace(c))]);
        var colon = cleaned.IndexOf(':');
        if (colon < 0)
        {
            return true;
        }

        // A colon after a '/', '?' or '#' belongs to the path, query or fragment: a relative URL.
        var separator = cleaned.AsSpan(0, colon).IndexOfAny('/', '?', '#');
        if (separator >= 0)
        {
            return true;
        }

        var scheme = cleaned[..colon];
        return scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            || scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
            || scheme.Equals("mailto", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class SafeLinkInlineRenderer(LinkInlineRenderer inner) : HtmlObjectRenderer<LinkInline>
    {
        protected override void Write(HtmlRenderer renderer, LinkInline link)
        {
            if (IsSafeUrl(link.Url))
            {
                inner.Write(renderer, link);
                return;
            }

            // The link's text, or the image's alt text, as text: no anchor, no img.
            var previous = renderer.EnableHtmlForInline;
            renderer.EnableHtmlForInline = false;
            renderer.WriteChildren(link);
            renderer.EnableHtmlForInline = previous;
        }
    }

    private sealed class SafeAutolinkInlineRenderer(AutolinkInlineRenderer inner) : HtmlObjectRenderer<AutolinkInline>
    {
        protected override void Write(HtmlRenderer renderer, AutolinkInline link)
        {
            if (link.IsEmail || IsSafeUrl(link.Url))
            {
                inner.Write(renderer, link);
                return;
            }

            renderer.WriteEscape(link.Url);
        }
    }
}
