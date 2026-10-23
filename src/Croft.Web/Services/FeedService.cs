using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Croft.Shared;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Croft.Web.Components.Layout;
using Croft.Web.Options;
using Microsoft.Extensions.Options;

namespace Croft.Web.Services;

/// <summary>
/// Builds the feed.xml Atom 1.0 document: the 20 newest published posts, newest first. Each
/// entry's link and id are the post's canonical URL, its summary the item-1 description
/// (SeoDescription: Excerpt → Subtitle → summary start) and its categories one element per
/// category-or-tag name. The API is read anonymously — the feed is the same published-only
/// document for every caller, so no user token is ever attached — and the published-only and
/// posts-only rules (published status with a publish time the clock has reached, and Kind ==
/// Post: pages are not posts) are applied here too, so drafts, scheduled posts and pages can
/// never leak into the feed whatever the API returns. XML is built with LINQ to XML, so
/// escaping is correct by construction.
/// </summary>
public class FeedService(IHttpClientFactory httpClientFactory, IOptions<BlogOptions> blogOptions,
    Branding branding, TimeProvider time) : IFeedService
{
    private const int MaxEntries = PublicPageSizes.FeedEntries;
    private static readonly XNamespace AtomNs = "http://www.w3.org/2005/Atom";

    public async Task<string> BuildXmlAsync(CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient("BlogApi");

        // The 20 newest fit one small page of the public listing (already published-only and
        // newest-first), unlike the sitemap's every-post document.
        var result = await client.GetFromJsonAsync<PagedResult<PostSummaryDto>>(
            PublicReadRoutes.Posts(new PostListRequest(Page: 1, PageSize: MaxEntries)), ct);

        var now = time.GetUtcNow().UtcDateTime;
        var posts = (result?.Items ?? [])
            .Where(p => p.Kind == PostKind.Post && p.Status == PostStatus.Published && p.PublishedAt <= now)
            .OrderByDescending(p => p.PublishedAt)
            .Take(MaxEntries)
            .ToList();

        // Absolute URLs come from Blog:BaseUrl like every canonical link — trimmed, because local
        // dev configures the value with a trailing slash and each path adds its own.
        var origin = blogOptions.Value.BaseUrl.TrimEnd('/');

        List<XElement> entries = [.. posts.Select(p => Entry(p, origin))];
        return Serialize(new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XElement(AtomNs + "feed",
                new XElement(AtomNs + "title", branding.Name),
                new XElement(AtomNs + "id", $"{origin}/feed.xml"),
                new XElement(AtomNs + "updated", FormatDate(posts.Count > 0 ? posts.Max(p => p.UpdatedAt) : now)),
                entries)));
    }

    private static XElement Entry(PostSummaryDto post, string origin)
    {
        var url = $"{origin}/post/{post.Slug}";
        var entry = new XElement(AtomNs + "entry",
            new XElement(AtomNs + "title", post.Title),
            new XElement(AtomNs + "link", new XAttribute("href", url)),
            new XElement(AtomNs + "id", url),
            new XElement(AtomNs + "updated", FormatDate(post.UpdatedAt)));

        // Published is the post's publish time. The published-only filter keeps only rows with a
        // publish time the clock has reached, so this guard is defensive only.
        if (post.PublishedAt is { } publishedAt)
            entry.Add(new XElement(AtomNs + "published", FormatDate(publishedAt)));

        entry.Add(
            new XElement(AtomNs + "author", new XElement(AtomNs + "name", post.AuthorName)),
            new XElement(AtomNs + "summary", SeoDescription.FromPost(post.Excerpt, post.Subtitle, post.Summary)));

        // One category element per category-or-tag name; a tag named like the category is not
        // repeated.
        HashSet<string> terms = new(StringComparer.Ordinal);
        if (post.Category is { } category && terms.Add(category.Name))
            entry.Add(CategoryElement(category.Name));

        foreach (var tag in post.Tags)
        {
            if (terms.Add(tag.Name))
                entry.Add(CategoryElement(tag.Name));
        }

        return entry;
    }

    private static XElement CategoryElement(string term) =>
        new(AtomNs + "category", new XAttribute("term", term));

    /// <summary>An RFC 3339 UTC instant — the same shape SeoHead's article dates use.</summary>
    private static string FormatDate(DateTime date) =>
        (date.Kind == DateTimeKind.Utc ? date : date.ToUniversalTime())
        .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>XDocument.Save to a writer whose Encoding is UTF-8, so the XML declaration matches
    /// the UTF-8 bytes the Web app serves (same as SitemapService).</summary>
    private static string Serialize(XDocument document)
    {
        using var text = new Utf8StringWriter();
        document.Save(text);
        return text.ToString();
    }

    private sealed class Utf8StringWriter() : StringWriter(CultureInfo.InvariantCulture)
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}
