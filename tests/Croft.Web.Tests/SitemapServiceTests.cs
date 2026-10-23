using System.Xml.Linq;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Croft.Web.Options;
using Croft.Web.Services;
using AwesomeAssertions;

namespace Croft.Web.Tests;

/// <summary>
/// The sitemap's content: the home page, every published post with its lastmod (its UpdatedAt as
/// an ISO 8601 date), and every category and tag with at least one published post. The published-only
/// filtering is the API's since brief 14b (<c>/api/sitemap</c>); the stub applies the same rule, and
/// <c>SitemapEndpointTests</c> in the API suite holds the real endpoint to it.
/// </summary>
public class SitemapServiceTests
{
    private static readonly XNamespace Ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
    private const string Origin = "https://example.com";
    private static readonly DateTime Updated = new(2026, 10, 5, 13, 45, 0, DateTimeKind.Utc);

    private static PostSummaryDto Post(string slug, PostStatus status, DateTime updatedAt) =>
        new(Guid.CreateVersion7(), slug, "A Title", null, null, "A summary.", 2, status,
            status == PostStatus.Draft ? null : updatedAt, updatedAt, "An Author", null, [], null, null, null);

    private static PageSummaryDto Page(string slug, PostStatus status, DateTime updatedAt) =>
        new(Guid.CreateVersion7(), slug, "A Page", status, updatedAt);

    private static async Task<(string Xml, StubBlogApiHandler Api)> BuildAsync(
        List<PostSummaryDto>? posts = null,
        List<CategoryDto>? categories = null,
        List<TagDto>? tags = null,
        List<PageSummaryDto>? pages = null,
        string baseUrl = Origin)
    {
        var api = new StubBlogApiHandler
        {
            Posts = posts ?? [],
            Categories = categories ?? [],
            Tags = tags ?? [],
            Pages = pages ?? []
        };
        var service = new SitemapService(
            new FakeHttpClientFactory(api),
            Microsoft.Extensions.Options.Options.Create(new BlogOptions { BaseUrl = baseUrl }));
        return (await service.BuildXmlAsync(), api);
    }

    private static string[] Locs(XDocument doc) =>
        [.. doc.Root!.Elements(Ns + "url").Elements(Ns + "loc").Select(e => e.Value)];

    [Fact]
    public async Task PublishedPost_IsListedWithItsLastmod()
    {
        var (xml, _) = await BuildAsync(posts: [Post("hello-world", PostStatus.Published, Updated)]);

        var doc = XDocument.Parse(xml);
        var entry = doc.Root!.Elements(Ns + "url")
            .Single(u => u.Element(Ns + "loc")!.Value == $"{Origin}/post/hello-world");
        entry.Element(Ns + "lastmod")!.Value.Should().Be("2026-10-05",
            "lastmod is the post's UpdatedAt as an ISO 8601 date");
    }

    [Fact]
    public async Task DraftPost_IsNotListed()
    {
        var (xml, _) = await BuildAsync(posts:
        [
            Post("visible", PostStatus.Published, Updated),
            Post("still-a-draft", PostStatus.Draft, Updated),
        ]);

        Locs(XDocument.Parse(xml)).Should().Contain($"{Origin}/post/visible")
            .And.NotContain($"{Origin}/post/still-a-draft", "drafts are not public and must stay out of the sitemap");
    }

    [Fact]
    public async Task ScheduledPost_IsNotListed()
    {
        var (xml, _) = await BuildAsync(posts:
        [
            Post("visible", PostStatus.Published, Updated),
            Post("not-yet", PostStatus.Scheduled, Updated),
        ]);

        Locs(XDocument.Parse(xml)).Should().Contain($"{Origin}/post/visible")
            .And.NotContain($"{Origin}/post/not-yet", "scheduled posts are not public yet and must stay out of the sitemap");
    }

    [Fact]
    public async Task PublishedPage_IsListedAtItsRootSlug_WithItsLastmod()
    {
        var (xml, api) = await BuildAsync(pages: [Page("about", PostStatus.Published, Updated)]);

        var doc = XDocument.Parse(xml);
        var entry = doc.Root!.Elements(Ns + "url")
            .Single(u => u.Element(Ns + "loc")!.Value == $"{Origin}/about");
        entry.Element(Ns + "lastmod")!.Value.Should().Be("2026-10-05",
            "lastmod is the page's UpdatedAt as an ISO 8601 date — the same wiring as posts");
        api.Requests.Select(r => r.PathAndQuery).Should().Equal(["/api/sitemap"],
            "pages come with the posts, categories and tags in the one slim read (brief 14b), never a whole-table read");
    }

    [Fact]
    public async Task UnpublishedPages_AreNotListed()
    {
        var (xml, _) = await BuildAsync(pages:
        [
            Page("visible", PostStatus.Published, Updated),
            Page("still-a-draft", PostStatus.Draft, Updated),
            Page("not-yet", PostStatus.Scheduled, Updated),
        ]);

        Locs(XDocument.Parse(xml)).Should().Contain($"{Origin}/visible")
            .And.NotContain($"{Origin}/still-a-draft", "drafts are not public and must stay out of the sitemap")
            .And.NotContain($"{Origin}/not-yet", "scheduled pages are not public yet and must stay out of the sitemap");
    }

    [Fact]
    public async Task HomeCategoryAndTagPages_AreListedOnlyWithPublishedPosts()
    {
        var (xml, _) = await BuildAsync(
            posts: [Post("hello-world", PostStatus.Published, Updated)],
            categories:
            [
                new(Guid.CreateVersion7(), "Technology", "technology", null, 2),
                new(Guid.CreateVersion7(), "Empty", "empty", null, 0),
            ],
            tags:
            [
                new(Guid.CreateVersion7(), "Blazor", "blazor", 1, true),
                new(Guid.CreateVersion7(), "Unused", "unused", 0, true),
            ]);

        // The exact set: the home page, the one category and the one tag whose PostCount says they
        // hold published posts — and nothing else (no admin pages, no empty category/tag pages).
        Locs(XDocument.Parse(xml)).Should().BeEquivalentTo(
        [
            $"{Origin}/",
            $"{Origin}/post/hello-world",
            $"{Origin}/category/technology",
            $"{Origin}/tag/blazor",
        ]);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("https://example.com/")]
    public async Task HomeUrl_HasOneSlashWhateverTheConfiguredBaseUrlShape(string baseUrl)
    {
        var (xml, _) = await BuildAsync(baseUrl: baseUrl);

        Locs(XDocument.Parse(xml)).Should().ContainSingle().Which.Should().Be("https://example.com/");
    }

    [Fact]
    public async Task SlugWithAmpersand_ProducesWellFormedXml()
    {
        var (xml, _) = await BuildAsync(posts: [Post("this&that", PostStatus.Published, Updated)]);

        var doc = XDocument.Parse(xml, LoadOptions.None); // throws if the ampersand broke the XML
        Locs(doc).Should().Contain($"{Origin}/post/this&that",
            "the URL text survives; the XML escaping is the serialiser's job");
    }

    [Fact]
    public async Task PostsPastTheFirstHundred_AreAllListed()
    {
        var posts = Enumerable.Range(1, 120)
            .Select(n => Post($"post-{n}", PostStatus.Published, Updated))
            .ToList();
        var (xml, api) = await BuildAsync(posts: posts);

        var doc = XDocument.Parse(xml);
        Locs(doc).Should().HaveCount(121, "every published post is listed, not just the first hundred");
        Locs(doc).Should().Contain(Enumerable.Range(1, 120).Select(n => $"{Origin}/post/post-{n}"));
        api.Requests.Select(r => r.PathAndQuery).Should().Equal(["/api/sitemap"],
            "every post comes in the one slim read (brief 14b): there is no paging to get wrong");
    }

    [Fact]
    public async Task ManyCategoriesAndTags_AreAllListed_FromTheOneSlimRead()
    {
        // 250 categories and 130 tags used to take five paged requests; now they come with everything else.
        var categories = Enumerable.Range(1, 250)
            .Select(n => new CategoryDto(Guid.CreateVersion7(), $"Category {n}", $"category-{n}", null, 1))
            .ToList();
        var tags = Enumerable.Range(1, 130)
            .Select(n => new TagDto(Guid.CreateVersion7(), $"Tag {n}", $"tag-{n}", 1, true))
            .ToList();

        var (xml, api) = await BuildAsync(categories: categories, tags: tags);

        var locs = Locs(XDocument.Parse(xml));
        locs.Should().Contain(Enumerable.Range(1, 250).Select(n => $"{Origin}/category/category-{n}"))
            .And.Contain(Enumerable.Range(1, 130).Select(n => $"{Origin}/tag/tag-{n}"),
                "the sitemap still lists every category and tag");
        api.Requests.Select(r => r.PathAndQuery).Should().Equal(["/api/sitemap"],
            "and asks the API once: the four paged lists it used to walk are not read any more");
    }

    [Fact]
    public async Task Xml_IsWellFormedWithTheSitemapNamespace()
    {
        var (xml, _) = await BuildAsync(
            posts: [Post("hello-world", PostStatus.Published, Updated)],
            categories: [new(Guid.CreateVersion7(), "Technology", "technology", null, 2)],
            tags: [new(Guid.CreateVersion7(), "Blazor", "blazor", 1, true)]);

        var doc = XDocument.Parse(xml, LoadOptions.None);
        doc.Root!.Name.Should().Be(Ns + "urlset", "the document is a sitemaps.org urlset");
        doc.Root.Elements().Should().OnlyContain(e => e.Name == Ns + "url");
        doc.Root.Elements(Ns + "url").Should().HaveCount(4).And.OnlyContain(u =>
            u.Elements().Select(e => e.Name.LocalName).All(name => name == "loc" || name == "lastmod"),
            "only loc and lastmod are requested — no changefreq or priority");
    }

    [Fact]
    public async Task Api_IsReadWithoutAuthorizationHeader()
    {
        var (xml, api) = await BuildAsync(posts: [Post("hello-world", PostStatus.Published, Updated)]);

        XDocument.Parse(xml);
        api.Requests.Should().NotBeEmpty().And.OnlyContain(r => r.Authorization == null,
            "the sitemap is the same published-only document for every caller, so no user token is attached");
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        // The real "BlogApi" named client always carries the ApiBaseUrl base address (Program.cs),
        // so the requests here reach the stub as absolute URLs exactly like in production.
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("http://blog-api.test/")
        };
    }
}
