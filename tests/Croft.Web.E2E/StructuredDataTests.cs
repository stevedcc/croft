using System.Text.Json;
using Croft.Shared.Enums;

namespace Croft.Web.E2E;

/// <summary>
/// Brief 10d item 2: a published post page carries both the BlogPosting JSON-LD and the
/// BreadcrumbList JSON-LD; both parse, and each declares the expected schema.org type.
/// Other reading pages (Home, Category, Tag, StaticPage) carry the Blog or WebPage
/// JSON-LD but never a BreadcrumbList.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class StructuredDataTests : StaticReadingTest
{
    private static readonly SemaphoreSlim DataGate = new(1, 1);
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];

    private static bool _dataReady;
    private static string _postSlug = string.Empty;
    private static string _categorySlug = string.Empty;
    private static string _tagSlug = string.Empty;
    private static string _pageSlug = string.Empty;

    [Test]
    public async Task PostPage_CarriesBlogPostingAndBreadcrumbListJsonLd()
    {
        await EnsureFixturesAsync();

        // Brief 10d item 2: read the rendered HTML directly so the assertion checks what a
        // crawler actually receives (an in-DOM <script> element), not what the page exposes
        // after any client-side enrichment.
        var response = await Page.GotoAsync($"{BaseUrl}/post/{_postSlug}");
        Assert.That(response!.Status, Is.EqualTo(200), "the post page must render");
        var html = await response.TextAsync();

        // Two script elements with type="application/ld+json" — one per JSON-LD block.
        var blocks = ExtractJsonLdBlocks(html);
        Assert.That(blocks.Count, Is.EqualTo(2),
            "the post page must carry exactly two application/ld+json blocks (BlogPosting + BreadcrumbList).");

        // Find each by @type so the assertion is independent of emission order.
        var types = blocks
            .Select(block => JsonDocument.Parse(block).RootElement.GetProperty("@type").GetString())
            .ToList();
        Assert.That(types, Does.Contain("BlogPosting"),
            "the post page must declare a BlogPosting JSON-LD");
        Assert.That(types, Does.Contain("BreadcrumbList"),
            "the post page must declare a BreadcrumbList JSON-LD (brief 10d item 2)");

        // Each block parses without error and the BreadcrumbList has at least Home + Post.
        var breadcrumb = JsonDocument.Parse(blocks.Single(b =>
            JsonDocument.Parse(b).RootElement.GetProperty("@type").GetString() == "BreadcrumbList"));
        var items = breadcrumb.RootElement.GetProperty("itemListElement").EnumerateArray().ToArray();
        Assert.That(items.Length, Is.GreaterThanOrEqualTo(2),
            "the BreadcrumbList must list at least the Home entry and the Post entry");
    }

    [Test]
    public async Task CategoryListing_DoesNotCarryBreadcrumbListJsonLd()
    {
        await EnsureFixturesAsync();

        var response = await Page.GotoAsync($"{BaseUrl}/category/{_categorySlug}");
        Assert.That(response!.Status, Is.EqualTo(200), "the category page must render");
        var html = await response.TextAsync();

        var blocks = ExtractJsonLdBlocks(html);
        foreach (var block in blocks)
        {
            using var doc = JsonDocument.Parse(block);
            Assert.That(doc.RootElement.GetProperty("@type").GetString(), Is.Not.EqualTo("BreadcrumbList"),
                "category listings never emit a BreadcrumbList (brief 10d item 2)");
        }
    }

    [Test]
    public async Task StaticPage_DoesNotCarryBreadcrumbListJsonLd()
    {
        await EnsureFixturesAsync();

        var response = await Page.GotoAsync($"{BaseUrl}/{_pageSlug}");
        Assert.That(response!.Status, Is.EqualTo(200), "the static page must render");
        var html = await response.TextAsync();

        var blocks = ExtractJsonLdBlocks(html);
        foreach (var block in blocks)
        {
            using var doc = JsonDocument.Parse(block);
            Assert.That(doc.RootElement.GetProperty("@type").GetString(), Is.Not.EqualTo("BreadcrumbList"),
                "static pages never emit a BreadcrumbList (brief 10d item 2)");
        }
    }

    /// <summary>Returns the JSON body of every <c>application/ld+json</c> script element on the
    /// page, in document order. The body is read verbatim from the HTML response (not the DOM)
    /// so the test sees what the crawler sees. The <c>+</c> in the script's <c>type</c>
    /// attribute is HTML-encoded as <c>&#x2B;</c> by the framework's HtmlEncoder, so we match
    /// either form.</summary>
    private static List<string> ExtractJsonLdBlocks(string html)
    {
        const string close = "</script>";
        // Either literal "+" or the HTML-encoded form "&#x2B;"; the framework's HtmlEncoder
        // emits the encoded form, but a manual edit or a future encoder change could land
        // the literal — accept both.
        string[] openers =
        [
            "<script type=\"application/ld+json\">",
            "<script type=\"application/ld&#x2B;json\">",
        ];
        var blocks = new List<string>();
        var cursor = 0;
        while (true)
        {
            var nextOpen = -1;
            var nextOpenLen = 0;
            foreach (var opener in openers)
            {
                var found = html.IndexOf(opener, cursor, StringComparison.Ordinal);
                if (found >= 0 && (nextOpen < 0 || found < nextOpen))
                {
                    nextOpen = found;
                    nextOpenLen = opener.Length;
                }
            }
            if (nextOpen < 0)
            {
                break;
            }
            var start = nextOpen + nextOpenLen;
            var end = html.IndexOf(close, start, StringComparison.Ordinal);
            if (end < 0)
            {
                break;
            }
            blocks.Add(html[start..end]);
            cursor = end + close.Length;
        }
        return blocks;
    }

    private async Task EnsureFixturesAsync()
    {
        if (_dataReady)
        {
            return;
        }

        await DataGate.WaitAsync();
        try
        {
            if (_dataReady)
            {
                return;
            }

            var category = await CreateCategoryAsync($"10d Cat {RunId}");
            _categorySlug = category.Slug;
            _tagSlug = $"10d{RunId}";
            _postSlug = (await PublishAsync($"10d Post {RunId}", "Brief 10d E2E body.", [_tagSlug], category.Id)).Slug;
            _pageSlug = (await PublishAsync($"10d About {RunId}", "Brief 10d E2E page.", kind: PostKind.Page)).Slug;
            _dataReady = true;
        }
        finally
        {
            DataGate.Release();
        }
    }
}