using System.Net;
using System.Xml.Linq;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests;

/// <summary>
/// GET /sitemap.xml on the Web host: application/xml, built from the API's slim sitemap read. The
/// published-only filtering is the API's since brief 14b; the stub applies the same rule.
/// </summary>
public class SitemapXmlTests
{
    private static readonly XNamespace Ns = "http://www.sitemaps.org/schemas/sitemap/0.9";

    // With a trailing slash exactly like start-local.sh configures Blog:BaseUrl (k8s has none).
    private const string ConfiguredBaseUrl = "https://example.com/";
    private static readonly DateTime Updated = new(2026, 10, 5, 13, 45, 0, DateTimeKind.Utc);

    [Fact]
    public async Task SitemapXml_ReturnsApplicationXmlListingPublishedContentOnly()
    {
        var api = new StubBlogApiHandler
        {
            Posts =
            [
                Post("hello-world", PostStatus.Published),
                Post("still-a-draft", PostStatus.Draft),
                Post("not-yet", PostStatus.Scheduled),
            ],
            Categories = [new(Guid.CreateVersion7(), "Technology", "technology", null, 1)],
            Tags = [new(Guid.CreateVersion7(), "Blazor", "blazor", 1, true)],
        };
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("Blog:BaseUrl", ConfiguredBaseUrl);
            builder.ConfigureServices(services =>
                services.AddHttpClient("BlogApi").ConfigurePrimaryHttpMessageHandler(() => api));
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/sitemap.xml");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.ToString().Should().Be("application/xml; charset=utf-8");
        var doc = XDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.Root!.Name.Should().Be(Ns + "urlset", "the document is a sitemaps.org urlset");

        // The exact set: home, the published post, the category and tag with a published post —
        // the draft and scheduled posts are absent, and so is every admin page.
        Locs(doc).Should().BeEquivalentTo(
        [
            "https://example.com/",
            "https://example.com/post/hello-world",
            "https://example.com/category/technology",
            "https://example.com/tag/blazor",
        ]);

        var post = doc.Root.Elements(Ns + "url")
            .Single(u => u.Element(Ns + "loc")!.Value == "https://example.com/post/hello-world");
        post.Element(Ns + "lastmod")!.Value.Should().Be("2026-10-05",
            "lastmod is the post's UpdatedAt as an ISO 8601 date");
    }

    [Fact]
    public async Task SitemapXml_ListsThePublishedPage_AtItsRootSlug()
    {
        var api = new StubBlogApiHandler
        {
            Pages =
            [
                new(Guid.CreateVersion7(), "about", "About", PostStatus.Published, Updated),
                new(Guid.CreateVersion7(), "still-a-draft", "Draft", PostStatus.Draft, Updated),
            ],
        };
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("Blog:BaseUrl", ConfiguredBaseUrl);
            builder.ConfigureServices(services =>
                services.AddHttpClient("BlogApi").ConfigurePrimaryHttpMessageHandler(() => api));
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/sitemap.xml");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var doc = XDocument.Parse(await response.Content.ReadAsStringAsync());
        var entry = doc.Root!.Elements(Ns + "url")
            .Single(u => u.Element(Ns + "loc")!.Value == "https://example.com/about");
        entry.Element(Ns + "lastmod")!.Value.Should().Be("2026-10-05",
            "lastmod is the page's UpdatedAt as an ISO 8601 date");
        Locs(doc).Should().NotContain("https://example.com/still-a-draft",
            "an unpublished page must stay out of the sitemap");
    }

    private static PostSummaryDto Post(string slug, PostStatus status) =>
        new(Guid.CreateVersion7(), slug, "A Title", null, null, "A summary.", 2, status,
            status == PostStatus.Draft ? null : Updated, Updated, "An Author", null, [], null, null, null);

    private static string[] Locs(XDocument doc) =>
        [.. doc.Root!.Elements(Ns + "url").Elements(Ns + "loc").Select(e => e.Value)];
}
