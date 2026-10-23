using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Croft.Api.Social;

/// <summary>
/// Turns a platform's reply content into the only HTML Croft stores and renders (brief 12a): <c>p</c>, <c>br</c> and <c>a</c>.
/// An <c>a</c> keeps its <c>href</c> only when it is http or https and always gets <c>rel="nofollow ugc noopener"</c>; every other
/// element is unwrapped to its text (script and style are dropped with their content, which is code, not text); every attribute
/// is dropped. The output is written here, never serialised from the parsed tree, so nothing the parser kept can leak into it.
/// </summary>
public static partial class SocialHtml
{
    private const string Rel = "nofollow ugc noopener";

    /// <summary>Mastodon's <c>content</c> (HTML, parsed with a real HTML parser: no regular expressions over markup).</summary>
    public static string FromMastodon(string html)
    {
        if (string.IsNullOrEmpty(html))
            return string.Empty;

        var document = new HtmlParser().ParseDocument($"<!DOCTYPE html><html><body>{html}</body></html>");
        var output = new StringBuilder();
        foreach (var child in document.Body!.ChildNodes)
            Write(child, output);

        return output.ToString();
    }

    private static void Write(INode node, StringBuilder output)
    {
        switch (node)
        {
            case IText text:
                output.Append(Encode(text.Data));
                break;

            case IElement element:
                switch (element.LocalName)
                {
                    case "script" or "style" or "template" or "noscript" or "head":
                        break;
                    case "br":
                        output.Append("<br>");
                        break;
                    case "p":
                        output.Append("<p>");
                        WriteChildren(element, output);
                        output.Append("</p>");
                        break;
                    case "a":
                        AppendLinkStart(output, SocialUrl.SafeHttp(element.GetAttribute("href")));
                        WriteChildren(element, output);
                        output.Append("</a>");
                        break;
                    default:
                        WriteChildren(element, output);
                        break;
                }

                break;
        }
    }

    /// <summary>Escapes the five characters that matter in HTML text and quoted attributes; every other character (umlauts, emoji) stays as it is.</summary>
    private static string Encode(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&#39;", StringComparison.Ordinal);

    private static void WriteChildren(INode parent, StringBuilder output)
    {
        foreach (var child in parent.ChildNodes)
            Write(child, output);
    }

    private static void AppendLinkStart(StringBuilder output, string? href)
    {
        output.Append("<a");
        if (href is not null)
            output.Append(" href=\"").Append(Encode(href)).Append('"');
        output.Append(" rel=\"").Append(Rel).Append("\">");
    }

    /// <summary>
    /// A Bluesky post's text: HTML-escaped, with the <paramref name="facets"/> (link, mention, tag) turned into the same links.
    /// A facet's offsets are UTF-8 byte offsets into the text, not UTF-16 indices. A facet that is out of range, overlaps another,
    /// cuts a character in two, spans a line break or carries an unsafe URL is ignored.
    /// </summary>
    public static string FromBluesky(string text, JsonElement facets)
    {
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var bytes = Encoding.UTF8.GetBytes(text);
        var html = new StringBuilder();
        var position = 0;
        foreach (var facet in ReadFacets(facets, bytes).OrderBy(f => f.Start))
        {
            if (facet.Start < position)
                continue; // overlaps the one before

            html.Append(Encode(Encoding.UTF8.GetString(bytes, position, facet.Start - position)));
            AppendLinkStart(html, facet.Href);
            html.Append(Encode(Encoding.UTF8.GetString(bytes, facet.Start, facet.End - facet.Start))).Append("</a>");
            position = facet.End;
        }

        html.Append(Encode(Encoding.UTF8.GetString(bytes, position, bytes.Length - position)));

        var paragraphs = BlankLine().Split(html.ToString())
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .Select(p => $"<p>{p.Replace("\n", "<br>", StringComparison.Ordinal)}</p>");
        return string.Concat(paragraphs);
    }

    private readonly record struct Facet(int Start, int End, string Href);

    private static IEnumerable<Facet> ReadFacets(JsonElement facets, byte[] bytes)
    {
        foreach (var facet in facets.Items())
        {
            var index = facet.Prop("index");
            if (index.Prop("byteStart") is not { ValueKind: JsonValueKind.Number } startValue
                || index.Prop("byteEnd") is not { ValueKind: JsonValueKind.Number } endValue
                || !startValue.TryGetInt32(out var start) || !endValue.TryGetInt32(out var end))
                continue;

            if (start < 0 || end <= start || end > bytes.Length || IsContinuationByte(bytes, start) || IsContinuationByte(bytes, end))
                continue;

            if (Array.IndexOf(bytes, (byte)'\n', start, end - start) >= 0)
                continue; // a link cannot span a line break

            var href = facet.Prop("features").Items().Select(Href).FirstOrDefault(h => h is not null);
            if (href is not null)
                yield return new Facet(start, end, href);
        }
    }

    /// <summary>True when <paramref name="offset"/> is inside a multi-byte character (a boundary is never a UTF-8 continuation byte).</summary>
    private static bool IsContinuationByte(byte[] bytes, int offset) => offset < bytes.Length && (bytes[offset] & 0xC0) == 0x80;

    private static string? Href(JsonElement feature) => feature.Str("$type") switch
    {
        "app.bsky.richtext.facet#link" => SocialUrl.SafeHttp(feature.Str("uri")),
        "app.bsky.richtext.facet#mention" when feature.Str("did") is { } did && Did().IsMatch(did) => $"https://bsky.app/profile/{did}",
        "app.bsky.richtext.facet#tag" when feature.Str("tag") is { Length: > 0 } tag => $"https://bsky.app/hashtag/{Uri.EscapeDataString(tag)}",
        _ => null,
    };

    [GeneratedRegex(@"\Adid:[a-z]+:[A-Za-z0-9._:%-]{1,256}\z")]
    private static partial Regex Did();

    /// <summary>A blank line: a newline, optional spaces, and another newline (further blank lines included).</summary>
    [GeneratedRegex(@"\n[ \t]*\n(?:[ \t]*\n)*")]
    private static partial Regex BlankLine();
}
