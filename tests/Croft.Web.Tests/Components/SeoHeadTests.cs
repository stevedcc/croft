using System.Text.Json;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Croft.Web.Components.Pages;
using Croft.Web.Options;
using Croft.Web.Services;
using Bunit;
using Bunit.Rendering;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Croft.Web.Tests.Components;

/// <summary>
/// Brief 03e item 1: the post page's SEO head, observed exactly as a browser receives it (a
/// <see cref="HeadOutlet"/> rendered beside the page, like App.razor). Covers every mandated
/// tag for a post with and without a banner and with and without an excerpt, the
/// Excerpt → Subtitle → summary-start description fallback (SeoDescription), noindex for
/// non-published posts, and the BlogPosting JSON-LD surviving quotes and a
/// <c>&lt;/script&gt;</c> injection attempt in the title.
/// </summary>
public class SeoHeadTests : BlogBunitContext
{
    private static readonly DateTime Published = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Created = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Updated = new(2026, 10, 9, 8, 30, 0, DateTimeKind.Utc);

    private string BlogName =>
        Services.GetRequiredService<Croft.Web.Branding>().Name;

    private string Origin =>
        Services.GetRequiredService<IOptions<BlogOptions>>().Value.BaseUrl.TrimEnd('/');

    [Fact]
    public async Task PublishedPost_WithBannerAndExcerpt_EmitsEveryHeadTag()
    {
        var cut = await RenderPostAsync(Post(excerpt: "The excerpt.", banner: Banner(), tags: [DotNet(), Blazor()], category: FieldNotes()));

        await cut.WaitForAssertionAsync(() =>
        {
            cut.Find("link[rel='canonical']").GetAttribute("href").Should().Be($"{Origin}/post/the-post");
            cut.Find("meta[name='description']").GetAttribute("content").Should().Be("The excerpt.");
            cut.Find("meta[property='og:type']").GetAttribute("content").Should().Be("article");
            cut.Find("meta[property='og:title']").GetAttribute("content").Should().Be("The Post");
            cut.Find("meta[property='og:description']").GetAttribute("content").Should().Be("The excerpt.");
            cut.Find("meta[property='og:url']").GetAttribute("content").Should().Be($"{Origin}/post/the-post");
            cut.Find("meta[property='og:site_name']").GetAttribute("content").Should().Be(BlogName);
            cut.Find("meta[property='og:locale']").GetAttribute("content").Should().Be("en_GB");
            cut.Find("meta[property='og:image']").GetAttribute("content").Should().Be($"{Origin}/api/media/{BannerId}");
            cut.Find("meta[property='article:published_time']").GetAttribute("content").Should().Be("2026-10-08T12:00:00Z");
            cut.Find("meta[property='article:modified_time']").GetAttribute("content").Should().Be("2026-10-09T08:30:00Z");
            cut.FindAll("meta[property='article:tag']").Select(t => t.GetAttribute("content"))
                .Should().BeEquivalentTo([".NET", "Blazor"]);
            cut.Find("meta[property='article:section']").GetAttribute("content").Should().Be("Field Notes");
            cut.Find("meta[name='twitter:card']").GetAttribute("content").Should().Be("summary_large_image");
            cut.FindAll("meta[name='robots']").Should().BeEmpty("a published post is indexable");
        });
    }

    [Fact]
    public async Task PublishedPost_WithoutBanner_OmitsImageTags_AndUsesSummaryCard()
    {
        var cut = await RenderPostAsync(Post(excerpt: "The excerpt."));

        await cut.WaitForAssertionAsync(() =>
        {
            cut.FindAll("meta[property='og:image']").Should().BeEmpty("no banner, no og:image");
            cut.Find("meta[name='twitter:card']").GetAttribute("content").Should().Be("summary");

            using var doc = JsonDocument.Parse(cut.Find("script[type='application/ld+json']").TextContent);
            doc.RootElement.TryGetProperty("image", out _).Should()
                .BeFalse("the JSON-LD image is banner-only too");
        });
    }

    [Fact]
    public async Task PublishedPost_WithoutExcerpt_DescribesWithSubtitle()
    {
        var cut = await RenderPostAsync(Post(subtitle: "The subtitle stands in."));

        await cut.WaitForAssertionAsync(() =>
        {
            cut.Find("meta[name='description']").GetAttribute("content").Should().Be("The subtitle stands in.");
            cut.Find("meta[property='og:description']").GetAttribute("content").Should().Be("The subtitle stands in.");
        });
    }

    [Fact]
    public async Task PublishedPost_WithoutExcerptOrSubtitle_DescribesWithSummaryStart()
    {
        // 10-char words: the first 14 span 153 characters, so the cut lands after the 14th.
        var words = Enumerable.Range(0, 20).Select(i => new string((char)('a' + i), 10)).ToArray();
        var cut = await RenderPostAsync(Post(summary: string.Join(' ', words)));

        await cut.WaitForAssertionAsync(() =>
            cut.Find("meta[name='description']").GetAttribute("content")
                .Should().Be($"{string.Join(' ', words.Take(14))}…"));
    }

    [Fact]
    public async Task DraftPost_IsMarkedNoindex()
    {
        var cut = await RenderPostAsync(Post(status: PostStatus.Draft, publishedAt: null, excerpt: "The excerpt."));

        await cut.WaitForAssertionAsync(() =>
        {
            cut.Find("meta[name='robots']").GetAttribute("content").Should().Be("noindex");
            cut.Find("link[rel='canonical']").Should().NotBeNull("the draft page still carries its canonical URL");
            cut.FindAll("meta[property='article:published_time']").Should()
                .BeEmpty("a draft has no publish date to advertise");
        });
    }

    [Fact]
    public async Task ScheduledPost_IsMarkedNoindex()
    {
        var cut = await RenderPostAsync(Post(status: PostStatus.Scheduled,
            publishedAt: Published.AddDays(30), excerpt: "The excerpt."));

        await cut.WaitForAssertionAsync(() =>
            cut.Find("meta[name='robots']").GetAttribute("content").Should().Be("noindex"));
    }

    [Fact]
    public async Task BanneredPost_EmitsBlogPostingJsonLd()
    {
        var cut = await RenderPostAsync(Post(excerpt: "The excerpt.", banner: Banner(),
            tags: [DotNet()], category: FieldNotes()));

        JsonElement root = default;
        await cut.WaitForAssertionAsync(() =>
        {
            using var doc = JsonDocument.Parse(cut.Find("script[type='application/ld+json']").TextContent);
            root = doc.RootElement.Clone();
        });

        root.GetProperty("@context").GetString().Should().Be("https://schema.org");
        root.GetProperty("@type").GetString().Should().Be("BlogPosting");
        root.GetProperty("headline").GetString().Should().Be("The Post");
        root.GetProperty("description").GetString().Should().Be("The excerpt.");
        root.GetProperty("datePublished").GetString().Should().Be("2026-10-08T12:00:00Z");
        root.GetProperty("dateModified").GetString().Should().Be("2026-10-09T08:30:00Z");
        root.GetProperty("author").GetProperty("@type").GetString().Should().Be("Person");
        root.GetProperty("author").GetProperty("name").GetString().Should().Be("Alice Author");
        root.GetProperty("image").GetString().Should().Be($"{Origin}/api/media/{BannerId}");
        root.GetProperty("mainEntityOfPage").GetString().Should().Be($"{Origin}/post/the-post");
        root.GetProperty("publisher").GetProperty("@type").GetString().Should().Be("Organization");
        root.GetProperty("publisher").GetProperty("name").GetString().Should().Be(BlogName,
            "with no author configured the publisher is the site's name");
    }

    [Fact]
    public async Task JsonLd_PublisherIsTheSiteAuthor_WhenTheBrandingHasOne()
    {
        Services.AddSingleton(new Croft.Web.Branding { Name = "Test Blog", Author = "Example Author" });

        var cut = await RenderPostAsync(Post(excerpt: "The excerpt."));

        JsonElement root = default;
        await cut.WaitForAssertionAsync(() =>
        {
            using var doc = JsonDocument.Parse(cut.Find("script[type='application/ld+json']").TextContent);
            root = doc.RootElement.Clone();
        });

        root.GetProperty("publisher").GetProperty("name").GetString().Should().Be("Example Author",
            "the JSON-LD publisher is the site's author when one is configured");
    }

    /// <summary>
    /// Brief 10d item 3: the BlogPosting JSON-LD adds the publisher's <c>url</c> and the
    /// publisher's <c>logo</c> (an ImageObject whose <c>url</c> is the site's logo, from
    /// <see cref="Croft.Web.Branding.FileUrl"/>), recommended by Google for Article rich
    /// results. Both URLs are absolute and built from <c>Blog:BaseUrl</c> like the rest of
    /// the head's structured data.
    /// </summary>
    [Fact]
    public async Task BlogPosting_JsonLd_PublisherHasUrlAndLogo()
    {
        var cut = await RenderPostAsync(Post(excerpt: "The excerpt."));

        JsonElement root = default;
        await cut.WaitForAssertionAsync(() =>
        {
            using var doc = JsonDocument.Parse(cut.Find("script[type='application/ld+json']").TextContent);
            root = doc.RootElement.Clone();
        });

        var publisher = root.GetProperty("publisher");
        publisher.GetProperty("url").GetString().Should().Be($"{Origin}/",
            "the publisher url is the site root, built from Blog:BaseUrl");

        var logo = publisher.GetProperty("logo");
        logo.GetProperty("@type").GetString().Should().Be("ImageObject");
        logo.GetProperty("url").GetString().Should().Be($"{Origin}/branding/logo.svg?v=logohash",
            "the publisher logo is the site's served logo, hashed, with its absolute origin");
    }

    /// <summary>
    /// Brief 10d item 3: the BlogPosting JSON-LD's <c>author</c> carries a <c>url</c>
    /// derived from <c>Blog:BaseUrl</c> (the site root) when no separate author profile URL
    /// is configured. Per Google's Article structured-data docs, <c>author.url</c> is
    /// recommended but not required.
    /// </summary>
    [Fact]
    public async Task BlogPosting_JsonLd_AuthorHasUrlFromBaseUrl()
    {
        var cut = await RenderPostAsync(Post(excerpt: "The excerpt.", banner: Banner()));

        JsonElement root = default;
        await cut.WaitForAssertionAsync(() =>
        {
            using var doc = JsonDocument.Parse(cut.Find("script[type='application/ld+json']").TextContent);
            root = doc.RootElement.Clone();
        });

        root.GetProperty("author").GetProperty("url").GetString().Should().Be($"{Origin}/",
            "the author url falls back to the site root, built from Blog:BaseUrl");
    }

    /// <summary>
    /// Brief 11a item 4: the BlogPosting JSON-LD's author Person carries <c>sameAs</c> — the
    /// owner's profile URLs in the branding's order — so search engines can tie the author to
    /// their profiles.
    /// </summary>
    [Fact]
    public async Task BlogPosting_JsonLd_AuthorHasSameAs_ProfileUrlsInOrder()
    {
        Services.AddSingleton(new Croft.Web.Branding
        {
            Name = "Test Blog",
            Profiles =
            [
                new("mastodon", "https://example.social/@someone"),
                new("bluesky", "https://bsky.app/profile/example.com"),
                new("github", "https://github.com/someone"),
            ],
        });

        var cut = await RenderPostAsync(Post(excerpt: "The excerpt."));

        JsonElement root = default;
        await cut.WaitForAssertionAsync(() =>
        {
            using var doc = JsonDocument.Parse(cut.Find("script[type='application/ld+json']").TextContent);
            root = doc.RootElement.Clone();
        });

        string[] expected =
        [
            "https://example.social/@someone",
            "https://bsky.app/profile/example.com",
            "https://github.com/someone",
        ];
        root.GetProperty("author").GetProperty("sameAs").EnumerateArray()
            .Select(url => url.GetString()).Should().Equal(
                expected,
                "sameAs lists the owner's profiles in the branding's order");
    }

    /// <summary>
    /// Brief 11a item 4: without profiles in the branding the author Person omits
    /// <c>sameAs</c> entirely (not an empty array).
    /// </summary>
    [Fact]
    public async Task BlogPosting_JsonLd_AuthorOmitsSameAs_WithoutProfiles()
    {
        var cut = await RenderPostAsync(Post(excerpt: "The excerpt."));

        JsonElement root = default;
        await cut.WaitForAssertionAsync(() =>
        {
            using var doc = JsonDocument.Parse(cut.Find("script[type='application/ld+json']").TextContent);
            root = doc.RootElement.Clone();
        });

        root.GetProperty("author").TryGetProperty("sameAs", out _).Should()
            .BeFalse("no profiles in the branding means no sameAs property at all");
    }

    /// <summary>
    /// A profile URL is owner-supplied text. <c>Uri.TryCreate</c> accepts an https URL whose
    /// path holds <c>&lt;/script&gt;</c>, so nothing in the branding validation keeps it out:
    /// the JSON-LD serializer's escaping is what stops it closing the script element.
    /// </summary>
    [Fact]
    public async Task BlogPosting_JsonLd_SameAs_ProfileUrlWithScriptCloseTag_CannotBreakOutOfTheScript()
    {
        const string hostile = "https://example.social/@x/</script><script>alert(1)</script>";
        Services.AddSingleton(new Croft.Web.Branding
        {
            Name = "Test Blog",
            Profiles = [new("mastodon", hostile)],
        });

        var cut = await RenderPostAsync(Post(excerpt: "The excerpt."));

        JsonElement root = default;
        await cut.WaitForAssertionAsync(() =>
        {
            using var doc = JsonDocument.Parse(cut.Find("script[type='application/ld+json']").TextContent);
            root = doc.RootElement.Clone();
        });

        root.GetProperty("author").GetProperty("sameAs").EnumerateArray().Select(url => url.GetString())
            .Should().Equal([hostile], "the URL round-trips through the JSON unchanged");
        cut.FindAll("script").Select(script => script.TextContent).Should().NotContain(
            text => text.Contains("alert(1)") && !text.Contains("\"@context\""),
            "no script element was injected next to the JSON-LD");
        cut.Markup.Should().NotContain("</script><script>",
            "the raw close tag must be escaped inside the JSON-LD");
    }

    [Fact]
    public async Task JsonLd_SurvivesQuotesAndScriptCloseTagInTitle()
    {
        var title = """He said "stop" </script><script>alert("xss")</script>""";
        var cut = await RenderPostAsync(Post(title: title, excerpt: "The excerpt."));

        string raw = string.Empty;
        await cut.WaitForAssertionAsync(() =>
        {
            raw = cut.Find("script[type='application/ld+json']").TextContent;
            raw.Should().NotBeNullOrEmpty();
        });

        raw.Should().NotContain("</script>", "the payload must not be able to close its own script tag");
        using var doc = JsonDocument.Parse(raw);
        doc.RootElement.GetProperty("headline").GetString().Should().Be(title);
    }

    /// <summary>
    /// Brief 10d item 1: a post with tags and a category reaches the BlogPosting JSON-LD as
    /// <c>keywords</c> (tag names joined with ", ", in the order the head receives them) and
    /// <c>articleSection</c> (the category's name). Values come from SeoHead's existing
    /// <c>Tags</c>/<c>Section</c> parameters — no new API calls.
    /// </summary>
    [Fact]
    public async Task PublishedPost_EmitsKeywordsAndArticleSection_InBlogPosting()
    {
        var kubernetes = new TagDto(Guid.CreateVersion7(), "kubernetes", "kubernetes", 5, true);
        var raspberryPi = new TagDto(Guid.CreateVersion7(), "raspberry-pi", "raspberry-pi", 5, true);
        var optimisation = new CategoryDto(Guid.CreateVersion7(), "Optimisation", "optimisation", null, 1);
        var cut = await RenderPostAsync(Post(excerpt: "The excerpt.", tags: [kubernetes, raspberryPi],
            category: optimisation));

        JsonElement root = default;
        await cut.WaitForAssertionAsync(() =>
        {
            using var doc = JsonDocument.Parse(cut.Find("script[type='application/ld+json']").TextContent);
            root = doc.RootElement.Clone();
        });

        root.GetProperty("keywords").GetString().Should().Be("kubernetes, raspberry-pi",
            "tag names are joined with \", \" in the order SeoHead received them");
        root.GetProperty("articleSection").GetString().Should().Be("Optimisation");
    }

    /// <summary>
    /// Brief 10d item 1: a post with no tags and no category omits both <c>keywords</c> and
    /// <c>articleSection</c> from its BlogPosting (asserting absence, not empty string).
    /// </summary>
    [Fact]
    public async Task PostWithNoTagsAndNoCategory_OmitsKeywordsAndArticleSection()
    {
        var cut = await RenderPostAsync(Post(excerpt: "The excerpt."));

        JsonElement root = default;
        await cut.WaitForAssertionAsync(() =>
        {
            using var doc = JsonDocument.Parse(cut.Find("script[type='application/ld+json']").TextContent);
            root = doc.RootElement.Clone();
        });

        root.TryGetProperty("keywords", out _).Should().BeFalse(
            "no tags means the keywords property is omitted, not an empty string");
        root.TryGetProperty("articleSection", out _).Should().BeFalse(
            "no category means the articleSection property is omitted, not an empty string");
    }

    /// <summary>
    /// Brief 10d item 1: a tag named <c>&lt;/script&gt;&lt;b&gt;</c> must not be able to close the
    /// BlogPosting JSON-LD's script element. The page still has exactly one
    /// application/ld+json block per type, the payload parses, and the headline reaches the
    /// BlogPosting JSON-LD unchanged. Brief specifies the tag name; the title in the same
    /// payload also still escapes (the existing test).
    /// </summary>
    [Fact]
    public async Task JsonLd_SurvivesScriptCloseTagInTagName()
    {
        var nasty = new TagDto(Guid.CreateVersion7(), "</script><b>", "nasty", 1, true);
        var cut = await RenderPostAsync(Post(excerpt: "The excerpt.", tags: [nasty]));

        // Brief 10d item 2 added the BreadcrumbList to post pages; this test is about the
        // BlogPosting payload surviving the tag-name injection, so it parses the one whose
        // @type is BlogPosting (regardless of order).
        var scripts = cut.FindAll("script[type='application/ld+json']");
        string raw = string.Empty;
        await cut.WaitForAssertionAsync(() =>
        {
            foreach (var script in scripts)
            {
                using var doc = JsonDocument.Parse(script.TextContent);
                if (doc.RootElement.GetProperty("@type").GetString() == "BlogPosting")
                {
                    raw = script.TextContent;
                    return;
                }
            }
            throw new InvalidOperationException("No BlogPosting JSON-LD block was rendered.");
        });

        raw.Should().NotContain("</script>", "the tag name must not be able to close the JSON-LD's script element");
        raw.Should().NotContain("<b>", "the tag name's HTML must not survive as live HTML in the payload");

        using var doc = JsonDocument.Parse(raw);
        doc.RootElement.GetProperty("keywords").GetString().Should().Be("</script><b>",
            "the literal tag name reaches the parsed payload, encoded safely");
    }

    /// <summary>
    /// Brief 10d item 1: a post page never emits <c>&lt;meta name="keywords"&gt;</c>. Google
    /// ignores it and Bing treats a stuffed one as spam, so the head's structured data must be
    /// the only place keywords reach the document.
    /// </summary>
    [Fact]
    public async Task PostPage_DoesNotEmitMetaKeywords()
    {
        var cut = await RenderPostAsync(Post(excerpt: "The excerpt.", tags: [DotNet()], category: FieldNotes()));

        cut.FindAll("meta[name='keywords']").Should().BeEmpty(
            "post pages advertise keywords only through the BlogPosting JSON-LD, never as a meta tag");
    }

    /// <summary>
    /// Brief 10d item 2: a post with a category renders a second JSON-LD block of
    /// <c>@type: BreadcrumbList</c> with three <c>ListItem</c>s at positions 1, 2 and 3, all URLs
    /// built from <c>Blog:BaseUrl</c> (never the request's host). Item 1 of the seed post
    /// (the site) points at <c>{BaseUrl}/</c>; item 2 (the category) at
    /// <c>{BaseUrl}/category/{slug}</c>; item 3 (the post) at its canonical URL.
    /// </summary>
    [Fact]
    public async Task PublishedPost_WithCategory_EmitsBreadcrumbListWithThreeItems()
    {
        Services.AddSingleton<IOptions<BlogOptions>>(Microsoft.Extensions.Options.Options.Create(
            new BlogOptions { BaseUrl = "https://example.test/" }));

        var category = new CategoryDto(Guid.CreateVersion7(), "Optimisation", "optimisation", null, 1);
        var cut = await RenderPostAsync(Post(excerpt: "The excerpt.", category: category));

        var scripts = cut.FindAll("script[type='application/ld+json']");
        scripts.Should().HaveCount(2,
            "the post page carries both the BlogPosting and the BreadcrumbList JSON-LD blocks");

        // The Brief: parse each block, find the one whose @type is BreadcrumbList (the second
        // in the head's emission order, but parsed-once tests should not depend on order).
        JsonElement breadcrumb = default;
        await cut.WaitForAssertionAsync(() =>
        {
            foreach (var script in scripts)
            {
                using var doc = JsonDocument.Parse(script.TextContent);
                if (doc.RootElement.GetProperty("@type").GetString() == "BreadcrumbList")
                {
                    breadcrumb = doc.RootElement.Clone();
                    return;
                }
            }
            throw new InvalidOperationException("No BreadcrumbList JSON-LD block was rendered.");
        });

        breadcrumb.GetProperty("@context").GetString().Should().Be("https://schema.org");
        var items = breadcrumb.GetProperty("itemListElement").EnumerateArray().ToArray();
        items.Should().HaveCount(3);

        items[0].GetProperty("position").GetInt32().Should().Be(1);
        items[0].GetProperty("name").GetString().Should().Be(BlogName);
        items[0].GetProperty("item").GetString().Should().Be("https://example.test/",
            "position 1 is the site root, built from Blog:BaseUrl regardless of the request host");

        items[1].GetProperty("position").GetInt32().Should().Be(2);
        items[1].GetProperty("name").GetString().Should().Be("Optimisation");
        items[1].GetProperty("item").GetString().Should().Be("https://example.test/category/optimisation",
            "position 2 is the category URL, built from Blog:BaseUrl");

        items[2].GetProperty("position").GetInt32().Should().Be(3);
        items[2].GetProperty("name").GetString().Should().Be("The Post");
        items[2].GetProperty("item").GetString().Should().Be("https://example.test/post/the-post",
            "position 3 is the post's canonical URL, built from Blog:BaseUrl");
    }

    /// <summary>
    /// Brief 10d item 2: a post with no category emits a two-item BreadcrumbList (Home →
    /// Post), skipping the category entry entirely. Asserting the count and the positions
    /// keeps the test honest when SeoHead is later extended with more entries.
    /// </summary>
    [Fact]
    public async Task PublishedPost_WithoutCategory_EmitsBreadcrumbListWithTwoItems()
    {
        Services.AddSingleton<IOptions<BlogOptions>>(Microsoft.Extensions.Options.Options.Create(
            new BlogOptions { BaseUrl = "https://example.test/" }));

        var cut = await RenderPostAsync(Post(excerpt: "The excerpt."));

        JsonElement breadcrumb = default;
        await cut.WaitForAssertionAsync(() =>
        {
            var scripts = cut.FindAll("script[type='application/ld+json']");
            foreach (var script in scripts)
            {
                using var doc = JsonDocument.Parse(script.TextContent);
                if (doc.RootElement.GetProperty("@type").GetString() == "BreadcrumbList")
                {
                    breadcrumb = doc.RootElement.Clone();
                    return;
                }
            }
            throw new InvalidOperationException("No BreadcrumbList JSON-LD block was rendered.");
        });

        var items = breadcrumb.GetProperty("itemListElement").EnumerateArray().ToArray();
        items.Should().HaveCount(2,
            "no category means the breadcrumb is Home → Post only");

        items[0].GetProperty("position").GetInt32().Should().Be(1);
        items[0].GetProperty("item").GetString().Should().Be("https://example.test/");

        items[1].GetProperty("position").GetInt32().Should().Be(2);
        items[1].GetProperty("item").GetString().Should().Be("https://example.test/post/the-post");
    }

    /// <summary>
    /// Brief 10d follow-up (DeepSeek review): a post whose title and category name contain the
    /// characters most likely to break out of the JSON-LD's <c>application/ld+json</c> script
    /// block — <c>&lt;/script&gt;</c>, a literal <c>"</c> and a literal <c>&lt;</c> — must
    /// still render a safe BreadcrumbList: no raw <c>&lt;/script&gt;</c> inside the block (which
    /// would terminate the script element), the block must parse, and every <c>name</c> value
    /// must round-trip exactly. SeoHead's <c>JsonSerializer.Serialize(payload)</c>
    /// uses the default encoder (the brief calls out <c>UnsafeRelaxedJsonEscaping</c> as the
    /// counter-example — its HTML-friendly encoder would emit a literal <c>&lt;</c> and break
    /// this test).
    /// </summary>
    [Fact]
    public async Task BreadcrumbList_StaysSafeWhenTitleAndCategoryContainHtmlSpecialChars()
    {
        Services.AddSingleton<IOptions<BlogOptions>>(Microsoft.Extensions.Options.Options.Create(
            new BlogOptions { BaseUrl = "https://example.test/" }));

        const string nastyTitle = "He said \"stop\" </script><script>alert(\"xss\")</script><b>";
        const string nastyCategory = "<b>Optimisation</b> & \"fast\"</script>";
        var category = new CategoryDto(Guid.CreateVersion7(), nastyCategory, "optimisation", null, 1);
        var cut = await RenderPostAsync(Post(title: nastyTitle, excerpt: "The excerpt.", category: category));

        // Two script blocks, one BreadcrumbList — pick it by @type so the test does not depend
        // on emission order.
        var scripts = cut.FindAll("script[type='application/ld+json']");
        scripts.Should().HaveCount(2);

        string raw = string.Empty;
        await cut.WaitForAssertionAsync(() =>
        {
            foreach (var script in scripts)
            {
                using var doc = JsonDocument.Parse(script.TextContent);
                if (doc.RootElement.GetProperty("@type").GetString() == "BreadcrumbList")
                {
                    raw = script.TextContent;
                    return;
                }
            }
            throw new InvalidOperationException("No BreadcrumbList JSON-LD block was rendered.");
        });

        // The raw text content of the BreadcrumbList script must not contain a literal
        // </script> — otherwise the rendered HTML closes the JSON-LD's script element
        // early and a second malformed <script> block follows.
        raw.Should().NotContain("</script>",
            "the BreadcrumbList payload must escape </script> so it cannot close its own script element");

        // The block parses and every name round-trips to the literal value the head received.
        // The default JsonSerializer encoder writes <, > and & as \u003c / \u003e / \u0026,
        // so the parsed JSON values are the original characters.
        using var parsed = JsonDocument.Parse(raw);
        var items = parsed.RootElement.GetProperty("itemListElement").EnumerateArray().ToArray();
        items.Should().HaveCount(3, "the post has a category, so the breadcrumb is Home → Category → Post");
        items[1].GetProperty("name").GetString().Should().Be(nastyCategory,
            "the category name round-trips exactly (with <, &, \" and </script>) through JSON parsing");
        items[2].GetProperty("name").GetString().Should().Be(nastyTitle,
            "the post title round-trips exactly (with <, \", and </script>) through JSON parsing");
    }

    /// <summary>
    /// Brief 10d item 2: pages that aren't post pages never emit a BreadcrumbList. The site's
    /// home, category, tag and static pages all carry the Blog or WebPage JSON-LD, never a
    /// BreadcrumbList. Each rendering needs its own <see cref="HeadOutlet"/> — Blazor's
    /// <see cref="Microsoft.AspNetCore.Components.Sections.SectionRegistry"/> allows one
    /// subscriber per section per render tree — so the four pages are split across four tests,
    /// each with the same shared fixture.
    /// </summary>
    [Fact]
    public async Task Home_DoesNotEmitBreadcrumbList()
    {
        ConfigureNonPostFixtures();
        NoBreadcrumbListOn(await RenderHomeAsync());
    }

    [Fact]
    public async Task CategoryListing_DoesNotEmitBreadcrumbList()
    {
        ConfigureNonPostFixtures();
        NoBreadcrumbListOn(await RenderCategoryAsync("tech"));
    }

    [Fact]
    public async Task TagListing_DoesNotEmitBreadcrumbList()
    {
        ConfigureNonPostFixtures();
        NoBreadcrumbListOn(await RenderTagAsync("tag"));
    }

    [Fact]
    public async Task StaticPage_DoesNotEmitBreadcrumbList()
    {
        ConfigureNonPostFixtures();
        NoBreadcrumbListOn(await RenderStaticPageAsync("about"));
    }

    /// <summary>Seeds the non-post pages (Home, Category, Tag, StaticPage) with a fake API and
    /// the shared markdown renderer, so each test can render any of the four reading pages.</summary>
    private void ConfigureNonPostFixtures()
    {
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient
        {
            Categories = [new CategoryDto(Guid.CreateVersion7(), "Tech", "tech", null, 1)],
            Tags = [new TagDto(Guid.CreateVersion7(), "Tag", "tag", 1, true)],
            Posts = ElevenPosts(),
            Pages = [Page(excerpt: "About this site.")],
        });
        Services.AddSingleton<IMarkdownRenderer>(new MarkdownRenderer());
    }

    private static void NoBreadcrumbListOn(IRenderedComponent<ContainerFragment> cut)
    {
        foreach (var script in cut.FindAll("script[type='application/ld+json']"))
        {
            using var doc = JsonDocument.Parse(script.TextContent);
            doc.RootElement.GetProperty("@type").GetString().Should().NotBe("BreadcrumbList",
                "BreadcrumbList is post pages only");
        }
    }

    private async Task<IRenderedComponent<ContainerFragment>> RenderHomeAsync()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<HeadOutlet>(0);
            builder.CloseComponent();
            builder.OpenComponent<Home>(1);
            builder.CloseComponent();
        });

        await cut.WaitForAssertionAsync(() => cut.Find("link[rel='canonical']").Should().NotBeNull());
        return cut;
    }

    private async Task<IRenderedComponent<ContainerFragment>> RenderCategoryAsync(string slug)
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<HeadOutlet>(0);
            builder.CloseComponent();
            builder.OpenComponent<Category>(1);
            builder.AddAttribute(2, "Slug", slug);
            builder.CloseComponent();
        });

        await cut.WaitForAssertionAsync(() => cut.Find("link[rel='canonical']").Should().NotBeNull());
        return cut;
    }

    private async Task<IRenderedComponent<ContainerFragment>> RenderTagAsync(string slug)
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<HeadOutlet>(0);
            builder.CloseComponent();
            builder.OpenComponent<Tag>(1);
            builder.AddAttribute(2, "Slug", slug);
            builder.CloseComponent();
        });

        await cut.WaitForAssertionAsync(() => cut.Find("link[rel='canonical']").Should().NotBeNull());
        return cut;
    }

    private async Task<IRenderedComponent<ContainerFragment>> RenderStaticPageAsync(string slug)
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<HeadOutlet>(0);
            builder.CloseComponent();
            builder.OpenComponent<StaticPage>(1);
            builder.AddAttribute(2, "Slug", slug);
            builder.CloseComponent();
        });

        await cut.WaitForAssertionAsync(() => cut.Find("link[rel='canonical']").Should().NotBeNull());
        return cut;
    }

    private static List<PostSummaryDto> ElevenPosts() =>
        [.. Enumerable.Range(1, 11).Select(n => Summary(n))];

    private static PostSummaryDto Summary(int n) =>
        new(Guid.CreateVersion7(), $"post-{n}", $"Post {n}", null, null, "A summary.", 2,
            PostStatus.Published,
            new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc), "An Author", null, [], null, 4, 1);

    [Fact]
    public async Task PostWithNoDescriptionMaterial_OmitsDescriptionTags()
    {
        var cut = await RenderPostAsync(Post(summary: string.Empty));

        await cut.WaitForAssertionAsync(() =>
        {
            cut.FindAll("meta[name='description']").Should().BeEmpty();
            cut.FindAll("meta[property='og:description']").Should().BeEmpty();

            using var doc = JsonDocument.Parse(cut.Find("script[type='application/ld+json']").TextContent);
            doc.RootElement.TryGetProperty("description", out _).Should().BeFalse();
        });
    }

    [Fact]
    public async Task PublishedPage_EmitsWebPageJsonLd_AndWebsiteOgType()
    {
        // Brief 07g item 3's SEO head: canonical /<slug>, og:type website, and JSON-LD WebPage
        // with datePublished and dateModified — dateModified is the content-edit time.
        var cut = await RenderPageAsync(Page(excerpt: "The page's description.",
            contentEditedAt: new DateTime(2026, 10, 10, 7, 45, 0, DateTimeKind.Utc)));

        await cut.WaitForAssertionAsync(() =>
        {
            cut.Find("link[rel='canonical']").GetAttribute("href").Should().Be($"{Origin}/about");
            cut.Find("meta[property='og:type']").GetAttribute("content").Should().Be("website");
            cut.Find("meta[property='og:title']").GetAttribute("content").Should().Be("About");
            cut.Find("meta[property='og:url']").GetAttribute("content").Should().Be($"{Origin}/about");
            cut.Find("meta[name='description']").GetAttribute("content").Should().Be("The page's description.");
            cut.FindAll("meta[property^='article:']").Should()
                .BeEmpty("a page is not an article: no article: meta at all");
            cut.FindAll("meta[name='robots']").Should().BeEmpty("a published page is indexable");
        });

        JsonElement root = default;
        await cut.WaitForAssertionAsync(() =>
        {
            using var doc = JsonDocument.Parse(cut.Find("script[type='application/ld+json']").TextContent);
            root = doc.RootElement.Clone();
        });

        root.GetProperty("@context").GetString().Should().Be("https://schema.org");
        root.GetProperty("@type").GetString().Should().Be("WebPage");
        root.GetProperty("name").GetString().Should().Be("About");
        root.GetProperty("url").GetString().Should().Be($"{Origin}/about");
        root.GetProperty("description").GetString().Should().Be("The page's description.");
        root.GetProperty("datePublished").GetString().Should().Be("2026-10-08T12:00:00Z");
        root.GetProperty("dateModified").GetString().Should().Be("2026-10-10T07:45:00Z",
            "dateModified is ContentEditedAt (the content edit), not UpdatedAt (2026-10-09T08:30:00Z)");
    }

    [Fact]
    public async Task DraftPage_IsMarkedNoindex()
    {
        var cut = await RenderPageAsync(Page(status: PostStatus.Draft, publishedAt: null,
            excerpt: "The page's description."));

        await cut.WaitForAssertionAsync(() =>
            cut.Find("meta[name='robots']").GetAttribute("content").Should().Be("noindex"));
    }

    private static Guid BannerId { get; } = Guid.CreateVersion7();

    private static MediaAssetDto Banner() => new(
        BannerId, "banner.png", "banner.png", "image/png", $"/api/media/{BannerId}",
        MediaAssetScope.PostSpecific, null, "A banner");

    private static TagDto DotNet() => new(Guid.CreateVersion7(), ".NET", "net", 5, true);

    private static TagDto Blazor() => new(Guid.CreateVersion7(), "Blazor", "blazor", 5, true);

    private static CategoryDto FieldNotes() => new(Guid.CreateVersion7(), "Field Notes", "field-notes", "marker", 7);

    private static PostDto Post(
        string slug = "the-post",
        string title = "The Post",
        string? subtitle = null,
        string? excerpt = null,
        string? summary = null,
        PostStatus status = PostStatus.Published,
        DateTime? publishedAt = null,
        MediaAssetDto? banner = null,
        IReadOnlyList<TagDto>? tags = null,
        CategoryDto? category = null) => new(
        Guid.CreateVersion7(), slug, title, subtitle, "# The post\n\nBody paragraph.", excerpt, summary, status,
        status == PostStatus.Published ? Published : publishedAt,
        Created, Updated, "author-id", "Alice Author", banner, tags ?? [], category, 3);

    /// <summary>Renders the post page with a <see cref="HeadOutlet"/> beside it, exactly like
    /// App.razor, so the page's head output is observable in the rendered markup.</summary>
    private async Task<IRenderedComponent<ContainerFragment>> RenderPostAsync(PostDto post)
    {
        var api = new FakeBlogApiClient();
        api.PostsById[post.Id] = post;
        Services.AddSingleton<IBlogApiClient>(api);
        Services.AddSingleton<IMarkdownRenderer>(new MarkdownRenderer());

        var cut = Render(builder =>
        {
            builder.OpenComponent<HeadOutlet>(0);
            builder.CloseComponent();
            builder.OpenComponent<Post>(1);
            builder.AddAttribute(2, "Slug", post.Slug);
            builder.CloseComponent();
        });

        // The page loads asynchronously; wait until its head output is present so callers can
        // assert synchronously. RenderedFragment.Find throws while loading, which retriggers
        // the wait (same shape as PageTitleTests).
        await cut.WaitForAssertionAsync(() => cut.Find("link[rel='canonical']").Should().NotBeNull());
        return cut;
    }

    private static PostDto Page(
        string slug = "about",
        string title = "About",
        string? subtitle = null,
        string? excerpt = null,
        string? summary = null,
        PostStatus status = PostStatus.Published,
        DateTime? publishedAt = null,
        DateTime? contentEditedAt = null) => new(
        Guid.CreateVersion7(), slug, title, subtitle, "## The page\n\nBody paragraph.", excerpt, summary, status,
        status == PostStatus.Published ? Published : publishedAt,
        Created, Updated, "author-id", "Alice Author", null, [], null, 3,
        PostKind.Page, NavigationPlacement.None, 0, contentEditedAt ?? Updated);

    /// <summary>Renders the public page with a <see cref="HeadOutlet"/> beside it, exactly like
    /// App.razor (same shape as <see cref="RenderPostAsync"/>).</summary>
    private async Task<IRenderedComponent<ContainerFragment>> RenderPageAsync(PostDto page)
    {
        var api = new FakeBlogApiClient();
        api.Pages.Add(page);
        Services.AddSingleton<IBlogApiClient>(api);
        Services.AddSingleton<IMarkdownRenderer>(new MarkdownRenderer());

        var cut = Render(builder =>
        {
            builder.OpenComponent<HeadOutlet>(0);
            builder.CloseComponent();
            builder.OpenComponent<StaticPage>(1);
            builder.AddAttribute(2, "Slug", page.Slug);
            builder.CloseComponent();
        });

        await cut.WaitForAssertionAsync(() => cut.Find("link[rel='canonical']").Should().NotBeNull());
        return cut;
    }
}
