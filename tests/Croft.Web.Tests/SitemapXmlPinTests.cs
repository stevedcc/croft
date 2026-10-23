using AwesomeAssertions;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Croft.Web.Options;
using Croft.Web.Services;

namespace Croft.Web.Tests;

/// <summary>
/// Brief 14b item 2's pin: the sitemap document for a seeded set, byte for byte. It was written
/// BEFORE the sitemap moved to the slim <c>/api/sitemap</c> read, passed on the old four-list walk
/// (the one test of the item that is green first, on purpose) and must stay green after: the
/// document readers and crawlers get does not change when only the way it is read does.
/// The seed: two published posts, one published page, one category and one tag (each with
/// published posts), one draft, one scheduled post, plus an empty category and an empty tag
/// that must stay out.
/// </summary>
public class SitemapXmlPinTests
{
    private static PostSummaryDto Post(string slug, PostStatus status, DateTime updatedAt) =>
        new(Guid.CreateVersion7(), slug, "A Title", null, null, "A summary.", 2, status,
            status == PostStatus.Draft ? null : updatedAt, updatedAt, "An Author", null, [], null, null, null);

    [Fact]
    public async Task TheDocumentForTheSeededSet_IsByteForByteTheKnownOne()
    {
        var api = new StubBlogApiHandler
        {
            Posts =
            [
                Post("hello-world", PostStatus.Published, new DateTime(2026, 10, 5, 13, 45, 0, DateTimeKind.Utc)),
                Post("second-post", PostStatus.Published, new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc)),
                Post("still-a-draft", PostStatus.Draft, new DateTime(2026, 10, 6, 9, 0, 0, DateTimeKind.Utc)),
                Post("not-yet", PostStatus.Scheduled, new DateTime(2026, 10, 7, 9, 0, 0, DateTimeKind.Utc)),
            ],
            Pages = [new(Guid.CreateVersion7(), "about", "About", PostStatus.Published, new DateTime(2026, 8, 15, 23, 59, 0, DateTimeKind.Utc))],
            Categories =
            [
                new(Guid.CreateVersion7(), "Technology", "technology", null, 2),
                new(Guid.CreateVersion7(), "Empty", "empty", null, 0),
            ],
            Tags =
            [
                new(Guid.CreateVersion7(), "Blazor", "blazor", 1, true),
                new(Guid.CreateVersion7(), "Unused", "unused", 0, true),
            ],
        };
        var service = new SitemapService(
            new FakeHttpClientFactory(api),
            Microsoft.Extensions.Options.Options.Create(new BlogOptions { BaseUrl = "https://example.com/" }));

        var xml = await service.BuildXmlAsync();

        xml.Should().Be(string.Join('\n',
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>",
            "<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">",
            "  <url>",
            "    <loc>https://example.com/</loc>",
            "  </url>",
            "  <url>",
            "    <loc>https://example.com/post/hello-world</loc>",
            "    <lastmod>2026-10-05</lastmod>",
            "  </url>",
            "  <url>",
            "    <loc>https://example.com/post/second-post</loc>",
            "    <lastmod>2026-09-01</lastmod>",
            "  </url>",
            "  <url>",
            "    <loc>https://example.com/about</loc>",
            "    <lastmod>2026-08-15</lastmod>",
            "  </url>",
            "  <url>",
            "    <loc>https://example.com/category/technology</loc>",
            "  </url>",
            "  <url>",
            "    <loc>https://example.com/tag/blazor</loc>",
            "  </url>",
            "</urlset>"));
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("http://blog-api.test/"),
        };
    }
}
