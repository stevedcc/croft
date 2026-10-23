using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Croft.Shared.Warmup;

namespace Croft.Web.Services;

/// <summary>
/// The Web's warm-up requests (brief 14a item 2), built at run time from the site's own content
/// so it works for any site: the home page and the sitemap say which post, page, category and tag
/// exist, the post's HTML says which image it loads and what its title is. Each request is a plain
/// anonymous GET to this process; the later ones use what the earlier ones returned. A step whose
/// source is missing is skipped, not failed (an empty site warms the home page, the sitemap,
/// /categories, a search and the feed only).
/// </summary>
public sealed partial class WebWarmupPlan : IWarmupPlan
{
    private const string Home = "/";
    private const string Sitemap = "/sitemap.xml";
    private const string Post = "post";

    public IReadOnlyList<WarmupStep> Steps { get; } =
    [
        new(Home, _ => Home, KeepBody: true),
        new(Sitemap, _ => Sitemap, KeepBody: true),
        new(Post, results => PostPath(results.Body(Home)), KeepBody: true),
        new("post image", results => PostImagePath(results.Body(Post))),
        new("static page", results => SitemapPath(results.Body(Sitemap), SinglePage())),
        new("category", results => SitemapPath(results.Body(Sitemap), PathUnder("/category/"))),
        new("tag", results => SitemapPath(results.Body(Sitemap), PathUnder("/tag/"))),
        WarmupStep.Get("/categories"),
        new("search", results => SearchPath(results.Body(Post))),
        WarmupStep.Get("/feed.xml"),
    ];

    /// <summary>The first post link on the home page (usually the newest); any post warms the post page.</summary>
    private static string? PostPath(string? homeHtml) =>
        homeHtml is not null && PostLink().Match(homeHtml) is { Success: true } link
            ? $"/post/{link.Groups["slug"].Value}"
            : null;

    /// <summary>The first <c>/api/media/...</c> image of the post's HTML, exactly as written
    /// (HTML-decoded, query included), from an <c>src</c> or the first candidate of a <c>srcset</c>:
    /// the share-card meta tags name the unsized original, which no reader's page loads.</summary>
    private static string? PostImagePath(string? postHtml)
    {
        if (postHtml is null || ImageAttribute().Match(postHtml) is not { Success: true } image)
        {
            return null;
        }

        var value = WebUtility.HtmlDecode(image.Groups["value"].Value);
        return value.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)[0];
    }

    /// <summary>The first sitemap address (its path, never the host: that is Blog:BaseUrl) the filter accepts.</summary>
    private static string? SitemapPath(string? sitemapXml, Func<string, bool> accepts)
    {
        if (sitemapXml is null)
        {
            return null;
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(sitemapXml);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        return document.Descendants()
            .Where(element => element.Name.LocalName == "loc")
            .Select(loc => Uri.TryCreate(loc.Value.Trim(), UriKind.Absolute, out var uri) ? uri.AbsolutePath : null)
            .FirstOrDefault(path => path is not null && accepts(path));
    }

    // A page lives at the root, /{slug}: one segment, and not the home page itself.
    private static Func<string, bool> SinglePage() =>
        path => path.Length > 1 && path.IndexOf('/', 1) < 0;

    private static Func<string, bool> PathUnder(string prefix) =>
        path => path.StartsWith(prefix, StringComparison.Ordinal) && path.Length > prefix.Length;

    /// <summary>The first word of the post's title (its h1), or "blog" without a post.</summary>
    private static string SearchPath(string? postHtml)
    {
        var word = "blog";
        if (postHtml is not null && Heading().Match(postHtml) is { Success: true } heading)
        {
            var text = WebUtility.HtmlDecode(Tags().Replace(heading.Groups["text"].Value, string.Empty));
            if (FirstWord().Match(text) is { Success: true } first)
            {
                word = first.Value;
            }
        }

        return $"/search?q={Uri.EscapeDataString(word)}";
    }

    [GeneratedRegex(@"href=""/post/(?<slug>[^""/?#]+)""[\s>]")]
    private static partial Regex PostLink();

    [GeneratedRegex(@"\bsrc(?:set)?\s*=\s*""(?<value>/api/media/[^""]*)""")]
    private static partial Regex ImageAttribute();

    [GeneratedRegex(@"<h1\b[^>]*>(?<text>.*?)</h1>", RegexOptions.Singleline)]
    private static partial Regex Heading();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex FirstWord();
}
