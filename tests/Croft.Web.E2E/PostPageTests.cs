using System.Text.Json;

namespace Croft.Web.E2E;

/// <summary>
/// Task R3.4: the rebuilt post page (spec §5.4) — hero, body, the right-hand rail (TOC, tags)
/// whose visibility follows the CONTENT area width via the @container query (plan key rules:
/// rail only at content ≥ 1000px) and the Related card section after the body (brief 07d item 3).
/// Also pins ruling R3-4-a (TOC links are path-qualified; bare "#id" hrefs would leave the post
/// page), the highlight.js run after render, and brief 04's page-view beacon on the rebuilt page.
///
/// Fixture rules (R3.3's hand-off): the shared dev DB accumulates posts across runs, so every
/// fixture is unique per run and count-free assertions are used where the count is not
/// controlled by the test. Playwright's retrying expects and waits on concrete conditions are
/// used throughout (R3.3's flake guidance): the prerender → interactive DOM swap can detach
/// nodes observed by one-shot reads.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class PostPageTests : WebTest
{
    private static readonly SemaphoreSlim DataGate = new(1, 1);
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];

    private static readonly string MainTitle = $"R34 Main {RunId}";
    private static readonly string RelOneTitle = $"R34 Rel1 {RunId}";
    private static readonly string RelTwoTitle = $"R34 Rel2 {RunId}";
    private static readonly string CategoryName = $"R34 Cat {RunId}";
    private static readonly string TagName = $"r34tag{RunId}";

    private static bool _dataReady;
    private static string _mainSlug = string.Empty;
    private static string _relOneSlug = string.Empty;

    [Test]
    public async Task TheHero_ShowsTheSummedCountsOfTheAnnouncements_InItsTopLeftCorner()
    {
        await using var db = await OpenInstanceDatabaseAsync();
        var (post, _) = await DiscussionSeed.PublishLinkedAsync(ApiUrl, db, Guid.NewGuid().ToString("N")[..12], withCounts: true);
        await Page.SetViewportSizeAsync(1440, 900);

        await Page.GotoAsync($"{BaseUrl}/post/{post.Slug}");

        // Mastodon (5, 2, 1) + Bluesky (7, 3, 4).
        var pill = Page.Locator(".post-hero .social-counts");
        await Expect(pill).ToBeVisibleAsync();
        await Expect(pill).ToHaveAttributeAsync("aria-label", "12 likes, 5 reposts, 5 replies");
        await Expect(pill.Locator(".social-counts__item")).ToHaveTextAsync(["12", "5", "5"]);
        await Expect(pill).ToHaveCSSAsync("pointer-events", "none");
        var hero = (await Page.Locator(".post-hero").BoundingBoxAsync())!;
        var box = (await pill.BoundingBoxAsync())!;
        Assert.Multiple(() =>
        {
            Assert.That(box.X - hero.X, Is.EqualTo(18).Within(1), "the pill sits 18px from the hero's left edge");
            Assert.That(box.Y - hero.Y, Is.EqualTo(14).Within(1), "and 14px from its top");
            Assert.That(box.Width, Is.GreaterThan(40), "with the three numbers in it");
        });
    }

    [Test]
    public async Task PostPage_At1440_ShowsRailWithTocTagsAndRelated()
    {
        await EnsureFixturesAsync();
        await Page.SetViewportSizeAsync(1440, 900);
        await Page.GotoAsync($"{BaseUrl}/post/{_mainSlug}");

        // 1440px viewport − 260px sidebar − 56px main padding = 1124px of content width, so the
        // @container (min-width: 1000px) rules show the rail column (ruling R3-4-a's geometry).
        var rail = Page.Locator(".post-rail");
        await Expect(rail).ToBeVisibleAsync();
        await Expect(rail.Locator(".toc")).ToBeVisibleAsync();
        await Expect(rail.Locator(".toc__link")).ToHaveCountAsync(2);
        await Expect(rail.Locator(".tag-pills a", new LocatorLocatorOptions { HasText = $"#{TagName}" })).ToHaveCountAsync(1);
        // Related moved out of the rail into a card section after the body (brief 07d item 3);
        // per ruling the locators below are re-scoped to the page — the assertions keep their
        // meaning (the related links render on the page under their titles).
        await Expect(Page.Locator(".related-posts__link", new PageLocatorOptions { HasText = RelOneTitle })).ToHaveCountAsync(1);
        await Expect(Page.Locator(".related-posts__link", new PageLocatorOptions { HasText = RelTwoTitle })).ToHaveCountAsync(1);

        // The collapsed block only exists below the threshold; at 1440 the rail's TOC is the one.
        await Expect(Page.Locator(".toc-inline")).ToBeHiddenAsync();

        // Ruling R3-4-a: never a bare fragment href — it would resolve to "/#id" and leave the post.
        await Expect(Page.Locator(".toc a[href^='#']")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task PostPage_At1100WithSidebarOpen_HidesRailTocAndShowsInlineToc()
    {
        await EnsureFixturesAsync();
        await Page.SetViewportSizeAsync(1100, 800);
        await Page.GotoAsync($"{BaseUrl}/post/{_mainSlug}");

        // 1100px viewport with the 64px rail = 980px of content width — below the 1000px
        // threshold (ruling R3-4-a's geometry), with the sidebar open as an overlay.
        var menu = Page.Locator(".rail-toggle");
        await menu.ClickAsync();
        await Expect(menu).ToHaveAttributeAsync("aria-expanded", "true");

        await Expect(Page.Locator(".post-rail .toc")).ToBeHiddenAsync();
        await Expect(Page.Locator(".toc-inline")).ToBeVisibleAsync();
        await Expect(Page.Locator(".toc-inline .toc__link")).ToHaveCountAsync(2);
    }

    [Test]
    public async Task PostPage_TocLinkClick_ScrollsHeadingIntoView()
    {
        await EnsureFixturesAsync();
        await Page.SetViewportSizeAsync(1440, 900);
        await Page.GotoAsync($"{BaseUrl}/post/{_mainSlug}");

        var tocLink = Page.Locator($".post-rail .toc a[href='/post/{_mainSlug}#second-section']");
        // The TOC link is path-qualified (ruling R3-4-a) — there is exactly one for this heading.
        await Expect(tocLink).ToHaveCountAsync(1);

        await tocLink.ClickAsync();

        // The click must land on the same post page with the heading's anchor in the URL.
        await Page.WaitForURLAsync(
            url => url.StartsWith($"{BaseUrl}/post/{_mainSlug}#"),
            new PageWaitForURLOptions { Timeout = 10000 });

        var heading = Page.Locator("h2#second-section");
        // Wait on the concrete outcome before the plan's one-shot read (R3.3's flake guidance):
        // the heading is scrolled into the top band of the viewport.
        await Page.WaitForFunctionAsync(
            "sel => { const el = document.querySelector(sel); if (!el) return false; const r = el.getBoundingClientRect(); return r.top < 200 && r.bottom > 0; }",
            "h2#second-section");

        var box = await heading.BoundingBoxAsync();
        Assert.That(box, Is.Not.Null, "the target heading is not in the DOM after the TOC click.");
        Assert.That(box.Y, Is.LessThan(200),
            "the TOC link must scroll its heading into view (plan Task R3.4).");
    }

    [Test]
    public async Task PostPage_CodeBlock_GetsHljsClassAfterRender()
    {
        await EnsureFixturesAsync();
        await Page.SetViewportSizeAsync(1440, 900);
        await Page.GotoAsync($"{BaseUrl}/post/{_mainSlug}");

        // post.js's highlightAll runs after render and marks the block with hljs's classes.
        await Expect(Page.Locator("pre code.hljs")).ToHaveCountAsync(1);
    }

    [Test]
    public async Task PostPage_AnonymousVisit_IncrementsViewCountByOne()
    {
        // Brief 04's beacon must keep working on the rebuilt page (global constraint). The
        // helper pattern is AnalyticsViewCountTests's: wait for the beacon response during the
        // visit, then read the total back from the admin analytics page.
        var (slug, postId) = await CreatePublishedPostAsync($"R34 Beacon {RunId}", "Beacon fixture body.");

        await using (var anonContext = await Browser.NewContextAsync())
        {
            var anonPage = await anonContext.NewPageAsync();
            await VisitPostCountingBeaconsAsync(anonPage, $"{BaseUrl}/post/{slug}", visits: 1);
        }

        await AssertTotalViewsAsync(postId, expected: 1);
    }

    [Test]
    public async Task PostPage_ImageWithMarkdownTitle_IsCaptioned()
    {
        // Spec §5.4 content styling: "images scale to the column (rounded, with a caption from
        // the Markdown title if present)". The fixture's titled image must end up inside a
        // figure whose figcaption carries the title text (ruling R3-4-d: post.js wraps it).
        await EnsureFixturesAsync();
        await Page.SetViewportSizeAsync(1440, 900);
        await Page.GotoAsync($"{BaseUrl}/post/{_mainSlug}");

        await Expect(Page.Locator(".markdown-body figure img[title='Fallback banner']")).ToHaveCountAsync(1);
        await Expect(Page.Locator(".markdown-body figure figcaption")).ToHaveTextAsync("Fallback banner");
    }

    [Test]
    public async Task PostPage_RelatedLinkClick_ShowsNewPostAndCountsItsView()
    {
        // Critical finding (fix round 1): Blazor reuses the page component when navigating
        // post→post (Routes.razor has no @key), so the load must follow the route parameter —
        // otherwise the related link updates the URL while the old post's content, TOC and
        // beacon stay behind.
        await EnsureFixturesAsync();
        await Page.SetViewportSizeAsync(1440, 900);
        await Page.GotoAsync($"{BaseUrl}/post/{_mainSlug}");
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = MainTitle, Level = 1 }))
            .ToBeVisibleAsync();

        // The scroll-spy's aria-current marker is only set from OnAfterRender — i.e. once the
        // interactive circuit is attached and page components are being reused. Clicking before
        // that would be a plain page load and would not exercise the component-reuse path.
        await Expect(Page.Locator(".post-rail .toc__link[aria-current='true']")).ToHaveCountAsync(1);

        var relPostId = await PostIdBySlugAsync(_relOneSlug);
        var beacon = Page.WaitForResponseAsync(r =>
            r.Url.EndsWith("/api/analytics/pageview") && r.Request.Method == "POST");

        // Re-scoped out of .post-rail like the expects above (ruling): Related is a section after
        // the body now, the click semantics are unchanged.
        await Page.Locator(".related-posts__link", new PageLocatorOptions { HasText = RelOneTitle })
            .ClickAsync();
        await Page.WaitForURLAsync(
            url => url.StartsWith($"{BaseUrl}/post/{_relOneSlug}"),
            new PageWaitForURLOptions { Timeout = 10000 });

        // Outcome 1: the related post's own hero renders under its URL.
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = RelOneTitle, Level = 1 }))
            .ToBeVisibleAsync();

        // Outcome 2: the new post's view is counted — its beacon is sent and accepted.
        var response = await beacon;
        Assert.That(response.Status, Is.EqualTo(204), "View beacon was not accepted by the Web app.");
        Assert.That(response.Request.PostData, Does.Contain(relPostId.ToString()),
            "the beacon must carry the related post's id, not the previously shown one");

        await LoginAsync("admin@example.com", "password");
        await AssertTotalViewsAsync(relPostId, expected: 1);
    }

    /// <summary>
    /// Task R3.6 fix (race found by the mandated consecutive full E2E runs, then traced): Blazor's
    /// prerender → interactive DOM swap can still be in flight when Post.razor's OnAfterRender calls
    /// blogPost.initScrollSpy — the TOC nodes land milliseconds later (traced: tocBefore=0 at call
    /// time, 4 links right after), so initScrollSpy early-returns on the empty DOM and, with
    /// _scrollHandled latched, is never called again: the scroll-spy silently never starts on those
    /// loads and the aria-current marker never appears (~1 cold load in 3). The outcome asserted:
    /// EVERY load ends with the current section marked in the rail's TOC (spec §5.4 "the current
    /// section highlighted as you scroll"), which is also PostPage_RelatedLinkClick's
    /// interactive-ready signal.
    /// </summary>
    [Test]
    public async Task PostPage_ScrollSpy_MarksCurrentSection_OnEveryLoad()
    {
        await EnsureFixturesAsync();
        const int loads = 8;

        for (var load = 1; load <= loads; load++)
        {
            // A fresh context per load: each load is a cold visit, the condition under which the
            // prerender → interactive swap race shows up.
            await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new() { Width = 1440, Height = 900 }
            });
            var page = await context.NewPageAsync();
            await page.GotoAsync($"{BaseUrl}/post/{_mainSlug}");

            await Expect(page.Locator(".post-rail .toc__link[aria-current='true']")).ToHaveCountAsync(1,
                new LocatorAssertionsToHaveCountOptions { Timeout = 5000 });
        }
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

            await LoginAsync("admin@example.com", "password");

            await Page.GotoAsync($"{BaseUrl}/admin/tags");
            await WaitForInteractiveAsync(Page);
            await CreateCategoryAsync(CategoryName);

            // The main post carries the TOC's two headings, a code block, a titled image (the
            // caption fixture, spec §5.4) and enough text below the second heading that it can
            // scroll to the top of the viewport; the related posts share its tag (rank 2) and
            // category (rank 1) so they surface as related.
            var body =
                $"Fixture intro {RunId} with enough words to start the page comfortably.\n\n" +
                "## First section\n\n" +
                $"First section body {RunId} one. " + Filler(4) + "\n\n" +
                "## Second section\n\n" +
                $"Second section body {RunId} one.\n\n" +
                "```csharp\n" +
                "builder.Services.AddScoped<DraftState>();\n" +
                "```\n\n" +
                "![A pattern](/branding/fallback-banner.svg \"Fallback banner\")\n\n" +
                Filler(8);

            _mainSlug = await PublishPostAsync(MainTitle, body, TagName, CategoryName);
            _relOneSlug = await PublishPostAsync(RelOneTitle, $"Related fixture one {RunId}.", TagName, CategoryName);
            await PublishPostAsync(RelTwoTitle, $"Related fixture two {RunId}.", TagName, CategoryName);

            await SignOutAsync();
            _dataReady = true;
        }
        finally
        {
            DataGate.Release();
        }
    }

    private static string Filler(int paragraphs) =>
        string.Join("\n\n", Enumerable.Range(1, paragraphs).Select(n =>
            $"Paragraph {n}: when I opened the solution again after three years, the first thing " +
            "I noticed was how much state lived in places it had no business being, and nothing " +
            "told anything else that it had happened."));

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

    private async Task<string> PublishPostAsync(string title, string body, string? tagName = null, string? categoryName = null)
    {
        // Route/selector migration (rule 5-sanctioned): /admin → /admin/posts/new, the tag input
        // is the chip field, "Publish / Schedule" → "Publish now"; the "the public API returns the
        // post" assertion lives in SlugForPostAsync now.
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

        return await SlugForPostAsync(PostIdFromEditorUrl());
    }

    /// <summary>AnalyticsViewCountTests's fixture pattern: a post of its own, created via the admin UI.</summary>
    private async Task<(string Slug, Guid PostId)> CreatePublishedPostAsync(string title, string body)
    {
        await LoginAsync("admin@example.com", "password");
        var slug = await PublishPostAsync(title, body);

        await using var response = await Page.APIRequest.GetAsync($"{ApiUrl}/api/posts/{slug}");
        Assert.That(response.Status, Is.EqualTo(200), "The public API did not return the post.");
        using var doc = JsonDocument.Parse(await response.TextAsync());
        return (slug, doc.RootElement.GetProperty("id").GetGuid());
    }

    private async Task<Guid> PostIdBySlugAsync(string slug)
    {
        await using var response = await Page.APIRequest.GetAsync($"{ApiUrl}/api/posts/{slug}");
        Assert.That(response.Status, Is.EqualTo(200), $"The public API did not return the post for slug {slug}.");
        using var doc = JsonDocument.Parse(await response.TextAsync());
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    /// <summary>AnalyticsViewCountTests's beacon helper pattern: every visit must be counted once.</summary>
    private static async Task VisitPostCountingBeaconsAsync(IPage page, string url, int visits)
    {
        for (var visit = 0; visit < visits; visit++)
        {
            var beacon = page.WaitForResponseAsync(r =>
                r.Url.EndsWith("/api/analytics/pageview") && r.Request.Method == "POST");

            if (visit == 0)
                await page.GotoAsync(url);
            else
                await page.ReloadAsync();

            var response = await beacon;
            Assert.That(response.Status, Is.EqualTo(204), "View beacon was not accepted by the Web app.");
        }
    }

    /// <summary>AnalyticsViewCountTests's assertion pattern: read the total back from the admin page.</summary>
    private async Task AssertTotalViewsAsync(Guid postId, int expected)
    {
        // Route migration (rule 5-sanctioned, disclosed in the commit message): the old
        // /admin/analytics page is gone; the same step now drives the post's analytics page.
        await Page.GotoAsync($"{BaseUrl}/admin/posts/{postId}/analytics");

        await Expect(Page.GetByText($"Total views: {expected}", new PageGetByTextOptions { Exact = true }))
            .ToBeVisibleAsync();
    }
}
