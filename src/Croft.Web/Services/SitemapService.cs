using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Croft.Shared;
using Croft.Shared.Contracts;
using Croft.Web.Options;
using Microsoft.Extensions.Options;

namespace Croft.Web.Services;

/// <summary>
/// Builds the sitemap.xml document: the home page, every published post (<c>lastmod</c> is its
/// <c>UpdatedAt</c> as an ISO 8601 date), every published page at its root <c>/&lt;slug&gt;</c>
/// (the same <c>UpdatedAt</c> lastmod wiring), and every category and tag with at least one
/// published post. The API is read anonymously, once: <c>GET /api/sitemap</c> returns exactly those
/// rows (brief 14b; it replaced four paged walks over the posts, pages, categories and tags lists),
/// published-only on the API's side, so a draft or a scheduled post is not in the answer to begin with.
/// The document is the same for every caller, so no user token is ever attached.
/// </summary>
public class SitemapService(IHttpClientFactory httpClientFactory, IOptions<BlogOptions> blogOptions) : ISitemapService
{
    private static readonly XNamespace SitemapNs = "http://www.sitemaps.org/schemas/sitemap/0.9";

    public async Task<string> BuildXmlAsync(CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient("BlogApi");
        var sitemap = await client.GetFromJsonAsync<SitemapDto>(PublicReadRoutes.Sitemap(), ct)
            ?? throw new InvalidOperationException("The API answered the sitemap read with nothing.");

        // Absolute URLs come from Blog:BaseUrl like every canonical link — trimmed, because local
        // dev configures the value with a trailing slash and each path adds its own.
        var origin = blogOptions.Value.BaseUrl.TrimEnd('/');

        List<XElement> entries = [Url($"{origin}/")];
        entries.AddRange(sitemap.Posts.Select(post => Url($"{origin}/post/{post.Slug}", post.UpdatedAt)));

        // A page lives at the Web root, /<slug>, with no /post prefix.
        entries.AddRange(sitemap.Pages.Select(page => Url($"{origin}/{page.Slug}", page.UpdatedAt)));
        entries.AddRange(sitemap.Categories.Select(slug => Url($"{origin}/category/{slug}")));
        entries.AddRange(sitemap.Tags.Select(slug => Url($"{origin}/tag/{slug}")));

        return Serialize(new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XElement(SitemapNs + "urlset", entries)));
    }

    private static XElement Url(string loc, DateTime? updatedAt = null)
    {
        var entry = new XElement(SitemapNs + "url", new XElement(SitemapNs + "loc", loc));
        if (updatedAt is { } updated)
        {
            // lastmod is the UTC date in ISO 8601 form.
            var utc = updated.Kind == DateTimeKind.Utc ? updated : updated.ToUniversalTime();
            entry.Add(new XElement(SitemapNs + "lastmod", utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }

        return entry;
    }

    /// <summary>XDocument.Save to a writer whose Encoding is UTF-8, so the XML declaration matches
    /// the UTF-8 bytes the Web app serves.</summary>
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
