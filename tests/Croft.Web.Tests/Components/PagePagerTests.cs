using Croft.Web.Components;
using Bunit;
using AwesomeAssertions;

namespace Croft.Web.Tests.Components;

/// <summary>
/// Task R3.2: PagePager — ruling R3-R0-b: real ?page=N links (JavaScript-disabled paging) with the
/// current page marked aria-current="page". Task R3.3, ruling R3-3-1: the pager is purely
/// presentational — the caller passes its query via the optional QueryString parameter and the
/// links become ?{QueryString}&amp;page=N.
/// </summary>
public class PagePagerTests : BlogBunitContext
{
    [Fact]
    public void PagePager_BuildsPageQueryLinks_AndMarksCurrent()
    {
        var cut = Render<PagePager>(p => p
            .Add(x => x.Page, 2)
            .Add(x => x.TotalPages, 3));

        var links = cut.FindAll("a");
        links.Select(l => l.GetAttribute("href")).Should().Equal("?page=1", "?page=2", "?page=3");
        links.Single(l => l.GetAttribute("aria-current") == "page").GetAttribute("href")
            .Should().Be("?page=2", "the current page carries aria-current='page'");
        links.Where(l => l.HasAttribute("aria-current")).Should().HaveCount(1);
    }

    [Fact]
    public void PagePager_WithQueryString_PrefixesPageLinks()
    {
        // Task R3.3: the search page's pager must keep ?q= while paging (spec §5.3 "paged as on
        // Home"). The assertions are unchanged from the earlier URL-merging version; the input
        // moved to the ruling R3-3-1 QueryString parameter (the pager may not inject services).
        var cut = Render<PagePager>(p => p
            .Add(x => x.Page, 2)
            .Add(x => x.TotalPages, 3)
            .Add(x => x.QueryString, "q=hello"));

        var links = cut.FindAll("a").Select(l => l.GetAttribute("href"));
        links.Should().Equal("?q=hello&page=1", "?q=hello&page=2", "?q=hello&page=3");
    }

    [Theory]
    [InlineData("q=hello", "?q=hello&page=1")]
    [InlineData("?q=hello", "?q=hello&page=1")]
    [InlineData("&q=hello", "?q=hello&page=1")]
    [InlineData("", "?page=1")]
    [InlineData(null, "?page=1")]
    public void PagePager_QueryString_NormalizesLeadingPunctuation(string? queryString, string expected)
    {
        // Ruling R3-3-1: callers may pass either "?q=hello" or "q=hello"; empty means no query.
        var cut = Render<PagePager>(p => p
            .Add(x => x.Page, 1)
            .Add(x => x.TotalPages, 2)
            .Add(x => x.QueryString, queryString!));

        cut.FindAll("a")[0].GetAttribute("href").Should().Be(expected);
    }

    [Fact]
    public void PagePager_WithPathAndQueryString_BuildsFullLinks()
    {
        // Ruling R3-3-2: a page that is not the base path passes its own path, so the link never
        // depends on <base href="/"> resolution (query-only hrefs would land on "/").
        var cut = Render<PagePager>(p => p
            .Add(x => x.Page, 2)
            .Add(x => x.TotalPages, 3)
            .Add(x => x.Path, "/search")
            .Add(x => x.QueryString, "q=hello"));

        var links = cut.FindAll("a").Select(l => l.GetAttribute("href"));
        links.Should().Equal("/search?q=hello&page=1", "/search?q=hello&page=2", "/search?q=hello&page=3");
    }

    [Fact]
    public void PagePager_WithPathOnly_BuildsPathPageLinks()
    {
        var cut = Render<PagePager>(p => p
            .Add(x => x.Page, 1)
            .Add(x => x.TotalPages, 2)
            .Add(x => x.Path, "/category/foo"));

        cut.FindAll("a")[0].GetAttribute("href").Should().Be("/category/foo?page=1");
    }

    [Theory]
    [InlineData("/search", "q=hello", "/search?q=hello&page=1")]
    [InlineData("/search?", "&q=hello", "/search?q=hello&page=1")]
    [InlineData("/search?q=hello", "", "/search?q=hello&page=1")]
    [InlineData("/category/foo&", "", "/category/foo?page=1")]
    [InlineData("/", "", "/?page=1")]
    [InlineData(null, null, "?page=1")]
    public void PagePager_PathAndQueryString_Normalize(string? path, string? queryString, string expected)
    {
        // Ruling R3-3-2: callers may pass Path with or without trailing details and QueryString
        // with or without a leading "?" / "&"; both empty is the plain ?page=N form.
        var cut = Render<PagePager>(p => p
            .Add(x => x.Page, 1)
            .Add(x => x.TotalPages, 2)
            .Add(x => x.Path, path!)
            .Add(x => x.QueryString, queryString!));

        cut.FindAll("a")[0].GetAttribute("href").Should().Be(expected);
    }

    [Fact]
    public void PagePager_SinglePage_RendersNothing()
    {
        var cut = Render<PagePager>(p => p
            .Add(x => x.Page, 1)
            .Add(x => x.TotalPages, 1));

        cut.Markup.Should().BeEmpty("a one-page pager is noise");
    }
}
