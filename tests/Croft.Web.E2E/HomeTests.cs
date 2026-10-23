using System.Text.Json;
using System.Text.RegularExpressions;

namespace Croft.Web.E2E;

/// <summary>
/// Task R3.3: the rebuilt Home, Category, Tag and Search pages compose the Task R3.2 card
/// components (PostList/PagePager/PostCard). Fixture rules (brief hand-off + ruling R3-R0-d): the
/// shared dev DB accumulates posts across runs, so every list/count assertion creates what it
/// counts first — unique titles, a unique category name and a unique tag via a per-run id — and
/// exact card counts are derived from the public API instead of assuming an empty DB. The "category
/// with no published posts" fixture is created through the admin UI (login as admin → /admin/tags →
/// "New category"): the browser context has no authenticated path to the API on :5000
/// (SchedulingTests records why).
///
/// Assertions use Playwright's retrying expects (ToHaveCountAsync/ToHaveCSSAsync/…) and wait for
/// network idle after navigation: Blazor's interactive startup replaces the prerendered DOM once
/// (~120-170ms in, a full body swap), and one-shot reads of nodes resolved just before that swap
/// observe detached elements (Chromium returns "" from getComputedStyle on them). The expected
/// outcomes are unchanged; the assertions just survive the startup swap.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class HomeTests : WebTest
{
    private static readonly SemaphoreSlim DataGate = new(1, 1);
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];

    private static readonly string AlphaTitle = $"R33 {RunId} Alpha";
    private static readonly string BetaTitle = $"R33 {RunId} Beta";
    private static readonly string PostCatName = $"R33 Cat {RunId}";
    private static readonly string EmptyCatName = $"R33 Empty {RunId}";
    private static readonly string PageCatName = $"R33 Page {RunId}";
    private static readonly string TagName = $"r33tag{RunId}";

    private static bool _dataReady;
    private static string _postCatSlug = string.Empty;
    private static string _emptyCatSlug = string.Empty;
    private static string _pageCatSlug = string.Empty;
    private static string _tagSlug = string.Empty;

    [Test]
    public async Task HomePage_Page1_HasExactlyOneFeaturedCardThenListCards()
    {
        await EnsureFixturesAsync();
        await Page.GotoAsync(BaseUrl);

        // Kept from HomePageTests.HomePage_ShowsSamplePost (fixture inputs changed to unique
        // titles per the plan's rule; the assertions are kept and strengthened below).
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Latest Posts" })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = AlphaTitle })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = BetaTitle })).ToBeVisibleAsync();

        var total = await PublishedPostCountAsync();
        var expectedCards = Math.Min(total, 10);

        var cards = Page.Locator(".post-card");
        await Expect(cards).ToHaveCountAsync(expectedCards);
        await Expect(Page.Locator(".post-card--featured")).ToHaveCountAsync(1);
        await Expect(Page.Locator(".post-card--list")).ToHaveCountAsync(expectedCards - 1);
        await Expect(cards.First).ToHaveClassAsync(new Regex("post-card--featured"));

        // Spec §5.2: the newest published post is the Featured one (Beta is published last).
        await Expect(Page.Locator(".post-card--featured")).ToContainTextAsync(BetaTitle);
    }

    [Test]
    public async Task HomePage_Page2_HasNoFeaturedCard()
    {
        await EnsureFixturesAsync();
        await Page.GotoAsync($"{BaseUrl}/?page=2");
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Latest Posts" })).ToBeVisibleAsync();

        var total = await PublishedPostCountAsync();
        var expectedCards = Math.Clamp(total - 10, 0, 10);

        await Expect(Page.Locator(".post-card")).ToHaveCountAsync(expectedCards);
        await Expect(Page.Locator(".post-card--featured")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task PostWithoutBanner_ShowsFallbackBannerAndCategoryIcon()
    {
        await EnsureFixturesAsync();
        await Page.GotoAsync(BaseUrl);

        // Alpha is a published post without a banner; its card must show the fallback banner
        // (no icon on a list card), and its category chip links to the category page.
        var card = Page.Locator(".post-card", new PageLocatorOptions { HasText = AlphaTitle }).First;
        await Expect(card).ToBeVisibleAsync();

        await Expect(card.Locator(".banner-or-fallback__fallback"))
            .ToHaveCSSAsync("background-image", new Regex("/branding/fallback-banner\\.svg"));
        // A list card's tartan carries no icon (the chip shows the category); the Featured card's does.
        await Expect(card.Locator(".banner-or-fallback__icon")).ToHaveCountAsync(0);
        await Expect(card.Locator($".post-card__chip a[href='/category/{_postCatSlug}']")).ToHaveCountAsync(1);
    }

    [Test]
    public async Task UnknownCategorySlug_Returns404Page()
    {
        // Unique per run: the shared dev DB accumulates categories from other tests.
        await using var response = await Page.APIRequest.GetAsync($"{BaseUrl}/category/missing-{Guid.NewGuid():N}");

        Assert.That(response.Status, Is.EqualTo(404),
            "An unknown category slug must answer with HTTP 404 (NavigationManager.NotFound).");
        Assert.That(await response.TextAsync(), Does.Contain("This page has wandered off"),
            "The 404 response must render the not-found copy (spec §5.5).");
    }

    [Test]
    public async Task UnknownTagSlug_Returns404Page()
    {
        // Unique per run: the shared dev DB accumulates tags from other tests. Spec §5.3.
        await using var response = await Page.APIRequest.GetAsync($"{BaseUrl}/tag/missing-{Guid.NewGuid():N}");

        Assert.That(response.Status, Is.EqualTo(404),
            "An unknown tag slug must answer with HTTP 404 (NavigationManager.NotFound).");
        Assert.That(await response.TextAsync(), Does.Contain("This page has wandered off"),
            "The 404 response must render the not-found copy (spec §5.5).");
    }

    [Test]
    public async Task CategoryWithNoPublishedPosts_ShowsNoPostsYetAndIsAbsentFromSidebar()
    {
        await EnsureFixturesAsync();
        await Page.GotoAsync($"{BaseUrl}/category/{_emptyCatSlug}");

        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = EmptyCatName })).ToBeVisibleAsync();
        await Expect(Page.GetByText("· 0 posts")).ToBeVisibleAsync();
        await Expect(Page.GetByText("No posts yet", new PageGetByTextOptions { Exact = true })).ToBeVisibleAsync();

        // The sidebar lists only the top 12 in-use categories by published-post count, so whether
        // one specific category is on screen depends on the database (rule: tests must not depend
        // on database state). The rule under test has a data-independent form: a category with no
        // published posts is never listed (while the sidebar does list categories at all, so the
        // absence is not vacuous), and a category with published posts is in use and publicly
        // listed — checked through the public categories search, the same contract the sidebar
        // reads, not through a position in any capped or paged list.
        var sidebar = Page.Locator("#sidebar-nav");
        await Expect(sidebar.Locator("a.nav-item[href^='/category/']").First).ToBeVisibleAsync();
        await Expect(sidebar.GetByText(EmptyCatName)).ToHaveCountAsync(0);

        await using var listed = await Page.APIRequest.GetAsync(
            $"{ApiUrl}/api/categories?q={Uri.EscapeDataString(PostCatName)}&page=1&pageSize=10");
        Assert.That(listed.Status, Is.EqualTo(200), "The public categories search is not reachable.");
        using var doc = JsonDocument.Parse(await listed.TextAsync());
        var matches = doc.RootElement.GetProperty("items").EnumerateArray()
            .Where(c => c.GetProperty("name").GetString() == PostCatName)
            .ToList();
        Assert.That(matches, Has.Count.EqualTo(1),
            "the category with published posts must be publicly listed");
        Assert.That(matches[0].GetProperty("postCount").GetInt32(), Is.EqualTo(2),
            "the listed category carries its two published posts");
    }

    [Test]
    public async Task CategoryWithPosts_ShowsHeaderWithCountAndListCards()
    {
        await EnsureFixturesAsync();
        await Page.GotoAsync($"{BaseUrl}/category/{_postCatSlug}");

        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = PostCatName })).ToBeVisibleAsync();
        await Expect(Page.GetByText("· 2 posts")).ToBeVisibleAsync();
        await Expect(Page.Locator(".post-card", new PageLocatorOptions { HasText = AlphaTitle })).ToBeVisibleAsync();
        await Expect(Page.Locator(".post-card", new PageLocatorOptions { HasText = BetaTitle })).ToBeVisibleAsync();
        await Expect(Page.Locator(".post-card--featured")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task CategoryPaging_ClickingPage2_ShowsSecondPageOfSameList()
    {
        await EnsureFixturesAsync();

        // "R33 Page {runid}" holds exactly the 11 paging fixture posts, so its page has a real
        // pager. Clicking page 2 must land on /category/{slug}?page=2 — not on "/" (query-only
        // hrefs resolve against <base href="/">, ruling R3-3-2) — and show the second page of the
        // same list: the oldest fixture post "… 01".
        await Page.GotoAsync($"{BaseUrl}/category/{_pageCatSlug}");

        await Expect(Page.GetByText("· 11 posts")).ToBeVisibleAsync();
        await Expect(Page.Locator(".post-card")).ToHaveCountAsync(10);

        var page2 = Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "2", Exact = true });
        await Expect(page2).ToHaveAttributeAsync("href", new Regex(Regex.Escape($"/category/{_pageCatSlug}?page=2")));

        await page2.ClickAsync();
        await Page.WaitForURLAsync(url => url.Contains($"/category/{_pageCatSlug}?page=2"), new PageWaitForURLOptions { Timeout = 10000 });

        await Expect(Page.GetByText("· 11 posts")).ToBeVisibleAsync();
        await Expect(Page.Locator(".post-card")).ToHaveCountAsync(1);
        await Expect(Page.Locator(".post-card")).ToContainTextAsync($"R33sp sp{RunId} 01");
    }

    [Test]
    public async Task TagPage_ShowsHeaderWithCountAndListCards()
    {
        await EnsureFixturesAsync();
        await Page.GotoAsync($"{BaseUrl}/tag/{_tagSlug}");

        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = $"#{TagName}" })).ToBeVisibleAsync();
        await Expect(Page.GetByText("· 2 posts")).ToBeVisibleAsync();
        await Expect(Page.Locator(".post-card", new PageLocatorOptions { HasText = AlphaTitle })).ToBeVisibleAsync();
        await Expect(Page.Locator(".post-card", new PageLocatorOptions { HasText = BetaTitle })).ToBeVisibleAsync();
        await Expect(Page.Locator(".post-card--featured")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task SearchWithResults_ShowsResultsHeaderAndListCards()
    {
        await EnsureFixturesAsync();
        await Page.GotoAsync($"{BaseUrl}/search?q={RunId}");

        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = $"2 results for '{RunId}'" }))
            .ToBeVisibleAsync();
        await Expect(Page.Locator(".post-card", new PageLocatorOptions { HasText = AlphaTitle })).ToBeVisibleAsync();
        await Expect(Page.Locator(".post-card", new PageLocatorOptions { HasText = BetaTitle })).ToBeVisibleAsync();
        await Expect(Page.Locator(".post-card--featured")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task EmptySearch_ShowsNoResultsMessage()
    {
        await EnsureFixturesAsync();

        // An empty query and a query with no matches both show the "No results" empty state.
        await Page.GotoAsync($"{BaseUrl}/search?q=");
        await Expect(Page.GetByText("No results", new PageGetByTextOptions { Exact = true })).ToBeVisibleAsync();

        await Page.GotoAsync($"{BaseUrl}/search?q=zzznope{RunId}qqq");
        await Expect(Page.GetByText($"0 results for 'zzznope{RunId}qqq'")).ToBeVisibleAsync();
        await Expect(Page.GetByText("No results", new PageGetByTextOptions { Exact = true })).ToBeVisibleAsync();
    }

    [Test]
    public async Task SearchPaging_ClickingPage2_KeepsQuery()
    {
        await EnsureFixturesAsync();

        // "sp{runid}" matches exactly the 11 search-paging fixture posts, so the results page has
        // a real pager (page size 10). Clicking page 2 must keep ?q= (spec §5.3 "paged as on Home",
        // ruling R3-3-1) — never land on a bare ?page=2 with the search dropped.
        await Page.GotoAsync($"{BaseUrl}/search?q=sp{RunId}");

        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = $"11 results for 'sp{RunId}'" }))
            .ToBeVisibleAsync();
        await Expect(Page.Locator(".post-card")).ToHaveCountAsync(10);

        var page2 = Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "2", Exact = true });
        await Expect(page2).ToHaveAttributeAsync("href", new Regex(Regex.Escape($"q=sp{RunId}&page=2")));

        await page2.ClickAsync();
        await Page.WaitForURLAsync(url => url.Contains($"q=sp{RunId}&page=2"), new PageWaitForURLOptions { Timeout = 10000 });

        // Page 2 of the same 11-result list: 1 card left (page size 10), still from this search's
        // results, and the query is kept in the header. (Search orders by relevance, not date, so
        // which fixture post lands last is not deterministic — the category page's date-ordered
        // list pins that instead.)
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = $"11 results for 'sp{RunId}'" }))
            .ToBeVisibleAsync();
        await Expect(Page.Locator(".post-card")).ToHaveCountAsync(1);
        await Expect(Page.Locator(".post-card")).ToContainTextAsync($"sp{RunId}");
        await Expect(Page.Locator(".post-card--featured")).ToHaveCountAsync(0);
    }

    /// <summary>
    /// Task R3.6, ruling R3-3-3: a stale/out-of-range ?page=999 shows the empty state — and now
    /// also the pager, so there is a way back (PostList renders it whenever TotalPages &gt; 1, even
    /// with zero items). EnsureFixturesAsync publishes 13 posts, so the list has at least 2 pages.
    /// Empty-state copy stays as R3.3 delivered it.
    /// </summary>
    [Test]
    public async Task HomePage_OutOfRangePage_ShowsEmptyStateAndPagerBackToPage1()
    {
        await EnsureFixturesAsync();
        await Page.GotoAsync($"{BaseUrl}/?page=999");

        await Expect(Page.GetByText("Nothing published yet.", new PageGetByTextOptions { Exact = true }))
            .ToBeVisibleAsync();

        var page1 = Page.GetByRole(AriaRole.Navigation, new PageGetByRoleOptions { Name = "Pagination" })
            .GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = "1", Exact = true });
        await Expect(page1).ToHaveAttributeAsync("href", new Regex(Regex.Escape("?page=1")));

        await page1.ClickAsync();
        await Page.WaitForURLAsync(url => url.EndsWith("?page=1"), new PageWaitForURLOptions { Timeout = 10000 });
        await Expect(Page.Locator(".post-card").First).ToBeVisibleAsync();
    }

    /// <summary>
    /// One fixture set per run (unique via <see cref="RunId"/>), created through the admin UI, in a fixed order (11 banner-less "search paging" posts matching
    /// only sp{runid} first, then the two main posts, so no test's assertions depend on test order:
    /// two published posts without banner in a category "R33 Cat {runId}" (both tagged
    /// "r33tag{runid}"), plus an empty category "R33 Empty {runid}".
    /// </summary>
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

            await LoginAsync("admin@example.com", "password");

            await Page.GotoAsync($"{BaseUrl}/admin/tags");
            await WaitForInteractiveAsync(Page);
            await CreateCategoryAsync(PostCatName);
            await CreateCategoryAsync(EmptyCatName);
            await CreateCategoryAsync(PageCatName);

            for (var n = 1; n <= 11; n++)
            {
                await PublishPostAsync($"R33sp sp{RunId} {n:00}", $"Search paging fixture sp{RunId}.", null, PageCatName);
            }

            await PublishPostAsync(AlphaTitle, $"Fixture body {RunId} for the R3.3 page tests.", TagName, PostCatName);
            await PublishPostAsync(BetaTitle, $"Fixture body {RunId} for the R3.3 page tests.", TagName, PostCatName);

            _postCatSlug = await CategorySlugAsync(PostCatName);
            _emptyCatSlug = await CategorySlugAsync(EmptyCatName);
            _pageCatSlug = await CategorySlugAsync(PageCatName);
            _tagSlug = await TagSlugAsync(TagName);

            await Page.GotoAsync($"{BaseUrl}/logout");
            _dataReady = true;
        }
        finally
        {
            DataGate.Release();
        }
    }

    private async Task CreateCategoryAsync(string name)
    {
        await WaitForInteractiveAsync(Page);
        await Page.GetByLabel("New category").FillAsync(name);
        await Page.Locator(".mud-input-control", new PageLocatorOptions { Has = Page.GetByLabel("New category") })
            .Locator("button").First
            .ClickAsync();
        // Wait on the concrete outcome: the category appears as a row of the categories table.
        await Expect(Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = $"Icon for {name}" }))
            .ToBeVisibleAsync();
    }

    private async Task PublishPostAsync(string title, string body, string? tagName = null, string? categoryName = null)
    {
        // Route/selector migration (rule 5-sanctioned): /admin → /admin/posts/new, the tag input
        // is the chip field, "Publish / Schedule" → "Publish now".
        await OpenNewPostEditorAsync();

        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync(title);
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).BlurAsync();
        await Page.GetByLabel("Markdown").FillAsync(body);
        await Page.GetByLabel("Markdown").BlurAsync();
        if (tagName is not null)
        {
            await AddTagsAsync(tagName);
        }

        if (categoryName is not null)
        {
            // Select through the picker's search: the unfiltered list is a bounded window
            // (the first page of name-ordered categories), so a freshly created category can
            // sort outside it. Typing its unique name finds it wherever it sorts.
            var category = Page.GetByRole(AriaRole.Combobox, new PageGetByRoleOptions { Name = "Category" });
            await category.ClickAsync();
            await category.FillAsync(categoryName);
            await Page.GetByRole(AriaRole.Option, new PageGetByRoleOptions { Name = categoryName }).ClickAsync();
        }

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Publish now" }).ClickAsync();
        await Page.WaitForURLAsync(
            url => url.StartsWith($"{BaseUrl}/admin/posts/") && url.EndsWith("/edit"),
            new PageWaitForURLOptions { Timeout = 15000 });
    }

    private async Task<string> CategorySlugAsync(string name)
    {
        await using var response = await Page.APIRequest.GetAsync($"{ApiUrl}/api/categories");
        Assert.That(response.Status, Is.EqualTo(200), "The API's category list is not reachable on :5000.");
        using var doc = JsonDocument.Parse(await response.TextAsync());
        foreach (var category in doc.RootElement.EnumerateArray())
        {
            if (category.GetProperty("name").GetString() == name)
            {
                return category.GetProperty("slug").GetString()!;
            }
        }

        Assert.Fail($"Category '{name}' was not created through the admin UI.");
        return string.Empty;
    }

    private async Task<string> TagSlugAsync(string name)
    {
        await using var response = await Page.APIRequest.GetAsync($"{ApiUrl}/api/tags");
        Assert.That(response.Status, Is.EqualTo(200), "The API's tag list is not reachable on :5000.");
        using var doc = JsonDocument.Parse(await response.TextAsync());
        foreach (var tag in doc.RootElement.EnumerateArray())
        {
            if (tag.GetProperty("name").GetString() == name)
            {
                return tag.GetProperty("slug").GetString()!;
            }
        }

        Assert.Fail($"Tag '{name}' was not created through the admin UI.");
        return string.Empty;
    }

    /// <summary>The published-post total from the public API — the same source the pages page over.</summary>
    private async Task<int> PublishedPostCountAsync()
    {
        await using var response = await Page.APIRequest.GetAsync($"{ApiUrl}/api/posts?pageSize=1");
        Assert.That(response.Status, Is.EqualTo(200), "The API's post list is not reachable on :5000.");
        using var doc = JsonDocument.Parse(await response.TextAsync());
        return doc.RootElement.GetProperty("totalCount").GetInt32();
    }
}
