using System.Globalization;
using System.Net;
using System.Xml.Linq;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests;

/// <summary>
/// GET /feed.xml on the Web host: application/atom+xml, an Atom 1.0 feed of the 20 newest
/// published posts — newest first, entry link/id the canonical URL, summary the item-1
/// description (SeoDescription), categories from the category and tags. The stub API serves
/// unfiltered rows on purpose — the published-only filtering is real feed code (FeedService),
/// not the fake. The site's name comes from a branding folder (neutral fixture values; the
/// example.com values moved with the branding design).
/// </summary>
public class FeedXmlTests : IDisposable
{
    private static readonly XNamespace Ns = "http://www.w3.org/2005/Atom";

    // With a trailing slash exactly like start-local.sh configures Blog:BaseUrl (k8s has none).
    private const string ConfiguredBaseUrl = "https://example.com/";
    private const string ConfiguredName = "Example Blog";
    private const string Origin = "https://example.com";

    // The branding folder this test's site name comes from (Blog:BrandingPath).
    private readonly string _brandingFolder = CreateBrandingFolder(ConfiguredName);

    public void Dispose()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }

        Directory.Delete(_brandingFolder, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static string CreateBrandingFolder(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), $"blog-feed-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "branding.json"), $$"""{ "name": "{{name}}" }""");
        return path;
    }

    private static readonly DateTime Published = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Updated = new(2026, 10, 9, 8, 30, 0, DateTimeKind.Utc);

    [Fact]
    public async Task FeedXml_IsWellFormedAtomWithFeedLevelElements()
    {
        var api = new StubBlogApiHandler { Posts = [Post(updatedAt: Updated)] };
        using var client = CreateClient(api);

        using var response = await client.GetAsync("/feed.xml");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.ToString().Should().Be("application/atom+xml; charset=utf-8");
        var raw = await response.Content.ReadAsStringAsync();
        var doc = XDocument.Parse(raw, LoadOptions.None); // throws if the document is not well-formed
        doc.Root!.Name.Should().Be(Ns + "feed", "the document is an Atom 1.0 feed");
        doc.Root.Elements().Should().OnlyContain(e => e.Name.Namespace == Ns,
            "every element is in the Atom namespace");
        doc.Root.Element(Ns + "title")!.Value.Should().Be(ConfiguredName, "the feed is titled with the site's name");
        doc.Root.Element(Ns + "id")!.Value.Should().Be($"{Origin}/feed.xml");
        doc.Root.Element(Ns + "updated")!.Value.Should().Be("2026-10-09T08:30:00Z",
            "feed updated is the most recent modification across the entries (RFC 4287 4.2.15)");
    }

    [Fact]
    public async Task Entry_CarriesCanonicalLinkIdentityMetadataAuthorAndCategories()
    {
        var api = new StubBlogApiHandler
        {
            Posts =
            [
                Post(subtitle: "A subtitle", excerpt: "The excerpt.", summary: "The summary start.",
                    category: new(Guid.CreateVersion7(), "Field Notes", "field-notes", null, 3),
                    tags: [new(Guid.CreateVersion7(), ".NET", "net", 2, true), new(Guid.CreateVersion7(), "Blazor", "blazor", 4, true)]),
            ],
        };
        using var client = CreateClient(api);

        using var response = await client.GetAsync("/feed.xml");

        var entry = XDocument.Parse(await response.Content.ReadAsStringAsync()).Root!.Elements(Ns + "entry").Single();
        entry.Element(Ns + "title")!.Value.Should().Be("The Post");
        entry.Element(Ns + "link")!.Attribute("href")!.Value.Should().Be($"{Origin}/post/the-post",
            "the link is the post's canonical URL");
        entry.Element(Ns + "id")!.Value.Should().Be($"{Origin}/post/the-post",
            "the id is the canonical URL");
        entry.Element(Ns + "updated")!.Value.Should().Be("2026-10-09T08:30:00Z");
        entry.Element(Ns + "published")!.Value.Should().Be("2026-10-08T12:00:00Z");
        entry.Element(Ns + "author")!.Element(Ns + "name")!.Value.Should().Be("Alice Author");
        entry.Element(Ns + "summary")!.Value.Should().Be("The excerpt.",
            "the summary is the item-1 description: the excerpt wins over subtitle and summary");
        entry.Elements(Ns + "category").Select(c => c.Attribute("term")!.Value).Should()
            .Equal(["Field Notes", ".NET", "Blazor"], "one category element per category-or-tag name");
    }

    [Fact]
    public async Task Summary_FallsBackThroughSubtitleThenSummaryStart()
    {
        // 10-char words: the first 14 span 153 characters, so the cut lands after the 14th —
        // exactly the item-1 description (SeoDescriptionTests).
        var words = Enumerable.Range(0, 20).Select(i => new string((char)('a' + i), 10)).ToArray();
        var api = new StubBlogApiHandler
        {
            Posts =
            [
                Post(slug: "by-subtitle", title: "By Subtitle", subtitle: "The subtitle stands in."),
                Post(slug: "by-summary", title: "By Summary", summary: string.Join(' ', words)),
            ],
        };
        using var client = CreateClient(api);

        using var response = await client.GetAsync("/feed.xml");

        var entries = XDocument.Parse(await response.Content.ReadAsStringAsync()).Root!.Elements(Ns + "entry");
        entries.Single(e => e.Element(Ns + "title")!.Value == "By Subtitle")
            .Element(Ns + "summary")!.Value.Should().Be("The subtitle stands in.");
        entries.Single(e => e.Element(Ns + "title")!.Value == "By Summary")
            .Element(Ns + "summary")!.Value.Should().Be($"{string.Join(' ', words.Take(14))}…");
    }

    [Fact]
    public async Task FeedXml_ExcludesDraftsScheduledAndFuturePosts()
    {
        var api = new StubBlogApiHandler
        {
            Posts =
            [
                Post(slug: "visible"),
                Post(slug: "still-a-draft", status: PostStatus.Draft, publishedAt: null),
                Post(slug: "not-yet", status: PostStatus.Scheduled, publishedAt: Published.AddDays(30)),
                Post(slug: "future-dated", status: PostStatus.Published, publishedAt: DateTime.UtcNow.AddDays(2)),
            ],
        };
        using var client = CreateClient(api);

        using var response = await client.GetAsync("/feed.xml");

        var ids = XDocument.Parse(await response.Content.ReadAsStringAsync()).Root!
            .Elements(Ns + "entry").Elements(Ns + "id").Select(e => e.Value).ToList();
        ids.Should().Equal([$"{Origin}/post/visible"],
            "drafts and scheduled posts are not public and must stay out of the feed — so must a "
            + "post whose publish time is still ahead of the clock");
    }

    [Fact]
    public async Task FeedXml_ExcludesPages()
    {
        // A page is a kind of post and the API serves both to whoever may see them, but the feed
        // lists posts only — like its published-only filter, the kind filter is real feed code
        // (FeedService), not the stub's.
        var api = new StubBlogApiHandler
        {
            Posts =
            [
                Post(slug: "a-post"),
                Post(slug: "about", title: "About", kind: PostKind.Page),
            ],
        };
        using var client = CreateClient(api);

        using var response = await client.GetAsync("/feed.xml");

        var ids = XDocument.Parse(await response.Content.ReadAsStringAsync()).Root!
            .Elements(Ns + "entry").Elements(Ns + "id").Select(e => e.Value).ToList();
        ids.Should().Equal([$"{Origin}/post/a-post"],
            "pages never appear among posts — not in the feed");
    }

    [Fact]
    public async Task FeedXml_ListsNewestFirst_AndUpdatesToTheLatestChange()
    {
        // The rows arrive in the stub's seeded order (scrambled), so newest-first is the feed's
        // own ordering. The oldest post was edited most recently: feed updated is the most
        // recent modification across the entries (RFC 4287 4.2.15), so it follows that edit —
        // this fixture shape keeps the assertion discriminating (posts[0].UpdatedAt would give
        // 2026-10-09).
        var api = new StubBlogApiHandler
        {
            Posts =
            [
                Post(slug: "oldest", publishedAt: Published.AddDays(-4), updatedAt: Updated.AddDays(5)),
                Post(slug: "newest", publishedAt: Published, updatedAt: Updated),
                Post(slug: "middle", publishedAt: Published.AddDays(-2), updatedAt: Updated.AddDays(-5)),
            ],
        };
        using var client = CreateClient(api);

        using var response = await client.GetAsync("/feed.xml");

        var root = XDocument.Parse(await response.Content.ReadAsStringAsync()).Root!;
        root.Elements(Ns + "entry").Elements(Ns + "id").Select(e => e.Value).Should()
            .Equal([$"{Origin}/post/newest", $"{Origin}/post/middle", $"{Origin}/post/oldest"],
                "entries are ordered by publication time descending, like the public listing");
        root.Element(Ns + "updated")!.Value.Should().Be("2026-10-14T08:30:00Z",
            "feed updated is the most recent modification across the entries (RFC 4287 4.2.15)");
    }

    [Fact]
    public async Task FeedXml_KeepsOnlyTheTwentyNewest_InOneSmallPage()
    {
        // 25 published rows newest-first, exactly as the public listing returns them.
        var api = new StubBlogApiHandler
        {
            Posts = [.. Enumerable.Range(1, 25)
                .Select(n => Post(slug: $"post-{n}", title: $"Post {n}",
                    publishedAt: Published.AddDays(-n), updatedAt: Updated.AddDays(-n)))],
        };
        using var client = CreateClient(api);

        using var response = await client.GetAsync("/feed.xml");

        var ids = XDocument.Parse(await response.Content.ReadAsStringAsync()).Root!
            .Elements(Ns + "entry").Elements(Ns + "id").Select(e => e.Value).ToList();
        ids.Should().HaveCount(20, "the feed carries the 20 newest posts");
        ids.Should().Equal(Enumerable.Range(1, 20).Select(n => $"{Origin}/post/post-{n}"));
        api.Requests.Should().NotContain(r => r.PathAndQuery.Contains("page=2"),
            "the 20 newest fit one small page; there is no need to page through everything");
    }

    [Fact]
    public async Task TitleWithAmpersandAndLessThan_IsEscapedInRawXmlAndSurvivesRoundTrip()
    {
        const string title = "Fish & <Chips>";
        var api = new StubBlogApiHandler { Posts = [Post(title: title)] };
        using var client = CreateClient(api);

        using var response = await client.GetAsync("/feed.xml");

        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().Contain("Fish &amp; &lt;Chips", "the ampersand and the angle bracket are escaped in the raw XML");
        raw.Should().NotContain("Fish & <Chips", "the unescaped form must not appear in the raw XML");

        var entry = XDocument.Parse(raw, LoadOptions.None).Root!.Elements(Ns + "entry").Single();
        entry.Element(Ns + "title")!.Value.Should().Be(title, "the parsed value round-trips exactly");
    }

    [Fact]
    public async Task FeedXml_WithNoPublishedPosts_IsStillAValidAtomFeed()
    {
        var api = new StubBlogApiHandler
        {
            Posts = [Post(slug: "still-a-draft", status: PostStatus.Draft, publishedAt: null)],
        };
        using var client = CreateClient(api);

        using var response = await client.GetAsync("/feed.xml");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = XDocument.Parse(await response.Content.ReadAsStringAsync()).Root!;
        root.Name.Should().Be(Ns + "feed");
        root.Element(Ns + "title")!.Value.Should().Be(ConfiguredName);
        root.Element(Ns + "id")!.Value.Should().Be($"{Origin}/feed.xml");
        var updated = root.Element(Ns + "updated")!.Value;
        DateTime.Parse(updated, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal)
            .Kind.Should().NotBe(DateTimeKind.Unspecified, "updated is an RFC 3339 instant");
        root.Elements(Ns + "entry").Should().BeEmpty();
    }

    [Fact]
    public async Task Api_IsReadWithoutAuthorizationHeader()
    {
        var api = new StubBlogApiHandler { Posts = [Post()] };
        using var client = CreateClient(api);

        using var response = await client.GetAsync("/feed.xml");

        XDocument.Parse(await response.Content.ReadAsStringAsync());
        api.Requests.Should().NotBeEmpty().And.OnlyContain(r => r.Authorization == null,
            "the feed is the same published-only document for every caller, so no user token is attached");
    }

    private static PostSummaryDto Post(
        string slug = "the-post",
        string title = "The Post",
        string? subtitle = null,
        string? excerpt = null,
        string? summary = "A summary.",
        PostStatus status = PostStatus.Published,
        DateTime? publishedAt = null,
        DateTime? updatedAt = null,
        string author = "Alice Author",
        CategoryDto? category = null,
        IReadOnlyList<TagDto>? tags = null,
        PostKind kind = PostKind.Post) =>
        new(Guid.CreateVersion7(), slug, title, subtitle, excerpt, summary ?? string.Empty, 2, status,
            publishedAt ?? (status == PostStatus.Draft ? null : Published),
            updatedAt ?? Updated, author, category, tags ?? [], null, null, null, kind);

    // The factories the client calls are kept and disposed with the class: a dropped factory's host lives to process exit.
    private readonly List<WebApplicationFactory<Program>> _factories = [];

    private HttpClient CreateClient(StubBlogApiHandler api)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("Blog:BaseUrl", ConfiguredBaseUrl);
            builder.UseSetting("Blog:BrandingPath", _brandingFolder);
            builder.ConfigureServices(services =>
                services.AddHttpClient("BlogApi").ConfigurePrimaryHttpMessageHandler(() => api));
        });
        _factories.Add(factory);
        return factory.CreateClient();
    }
}
