namespace Croft.Web.E2E;

/// <summary>
/// Brief 09f item 4: the page-view beacon works on static pages. static-glue.js sends
/// <c>blogAnalytics.recordPageView</c> with the id the post page renders as data-post-id: once
/// per post view — a full load, or an enhanced navigation to another post — and never on any other
/// page. Outcomes asserted: the POSTs to /api/analytics/pageview the browser actually sent, and
/// the API's own view count per post (the admin analytics page) rising by exactly the views.
/// Each test uses its own anonymous browser context (admin visits are not counted).
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class StaticBeaconTests : StaticReadingTest
{
    private static readonly SemaphoreSlim DataGate = new(1, 1);
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];

    private static bool _dataReady;
    private static Published _postA = null!;
    private static Published _postB = null!;
    private static string _titleB = string.Empty;
    private static string _categorySlug = string.Empty;
    private static string _tag = string.Empty;
    private static string _pageSlug = string.Empty;

    [Test]
    public async Task FullLoadOfAPost_SendsExactlyOneBeacon()
    {
        await EnsureFixturesAsync();
        await using var context = await NewDesktopContextAsync();
        var page = await context.NewPageAsync();
        var beacons = CountBeacons(page);

        await page.GotoAsync($"{BaseUrl}/post/{_postA.Slug}");
        await WaitForInteractiveAsync(page);
        await beacons.WaitForCountAsync(1, "the beacon of the full load of a post");

        Assert.That(beacons.Count, Is.EqualTo(1), "one full load of a post is one view: one beacon.");
        Assert.That(beacons.Bodies[0], Does.Contain(_postA.Id.ToString()), "the beacon carries the post's id.");
    }

    [Test]
    public async Task EnhancedNavigationToAnotherPost_SendsExactlyOneMoreBeacon_ForThatPost()
    {
        await EnsureFixturesAsync();
        await using var context = await NewDesktopContextAsync();
        var page = await context.NewPageAsync();
        var beacons = CountBeacons(page);
        await page.GotoAsync($"{BaseUrl}/post/{_postA.Slug}");
        await WaitForInteractiveAsync(page);
        await page.EvaluateAsync("() => { window.__docMarker = 'one-document'; }");

        await page.Locator($"a.related-posts__link[href='/post/{_postB.Slug}']").ClickAsync();
        await page.WaitForURLAsync(url => url.EndsWith($"/post/{_postB.Slug}"));
        await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = _titleB, Level = 1 })).ToBeVisibleAsync();
        await Expect(page.Locator("html")).ToHaveAttributeAsync("data-interactive", "on");
        await beacons.WaitForCountAsync(2, "the beacon of the enhanced navigation to post B");

        Assert.That(await page.EvaluateAsync<string?>("() => window.__docMarker"), Is.EqualTo("one-document"),
            "the navigation must be an enhanced one.");
        Assert.That(beacons.Count, Is.EqualTo(2), "the first view and the enhanced navigation to post B: two beacons.");
        Assert.That(beacons.Bodies[1], Does.Contain(_postB.Id.ToString()), "the second beacon is for post B.");
        Assert.That(beacons.Bodies[1], Does.Not.Contain(_postA.Id.ToString()));
    }

    [Test]
    public async Task PagesThatAreNotPosts_SendNoBeacon_OnAFullLoad()
    {
        await EnsureFixturesAsync();
        await using var context = await NewDesktopContextAsync();
        var page = await context.NewPageAsync();
        var beacons = CountBeacons(page);

        foreach (var path in new[]
        {
            "/", $"/category/{_categorySlug}", $"/tag/{_tag}", "/categories",
            $"/search?q={RunId}", $"/{_pageSlug}",
        })
        {
            await page.GotoAsync($"{BaseUrl}{path}");
            await WaitForInteractiveAsync(page);
        }

        // A count of zero read right after a navigation is also what a beacon still on its way looks
        // like. Close the question with a positive: a post's full load sends its beacon after every
        // earlier one (requests of one page are in order), so exactly this one, and for this post,
        // means none of the non-post pages sent one.
        await page.GotoAsync($"{BaseUrl}/post/{_postA.Slug}");
        await WaitForInteractiveAsync(page);
        await beacons.WaitForCountAsync(1, "the beacon of the closing post load");
        Assert.That(beacons.Count, Is.EqualTo(1), "only the closing post's load sent a beacon: the non-post pages sent none.");
        Assert.That(beacons.Bodies[0], Does.Contain(_postA.Id.ToString()), "the one beacon is the closing post's.");
    }

    [Test]
    public async Task EnhancedNavigationToPagesThatAreNotPosts_SendsNoBeacon()
    {
        await EnsureFixturesAsync();
        await using var context = await NewDesktopContextAsync();
        var page = await context.NewPageAsync();
        var beacons = CountBeacons(page);
        await page.GotoAsync($"{BaseUrl}/post/{_postA.Slug}");
        await WaitForInteractiveAsync(page);
        await beacons.WaitForCountAsync(1, "the starting post view's beacon");
        Assert.That(beacons.Count, Is.EqualTo(1), "the starting post view is one beacon.");
        await page.EvaluateAsync("() => { window.__docMarker = 'one-document'; }");

        // The sidebar's Home link and Search link: enhanced navigations to non-post pages.
        await page.Locator("#sidebar-nav a.nav-item[href='/']").ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Latest Posts" })).ToBeVisibleAsync();
        await Expect(page.Locator("html")).ToHaveAttributeAsync("data-interactive", "on");
        await page.Locator("#sidebar-nav a.nav-item[href='/search']").ClickAsync();
        await Expect(page.Locator("#search-field")).ToBeVisibleAsync();
        await Expect(page.Locator("html")).ToHaveAttributeAsync("data-interactive", "on");

        Assert.That(await page.EvaluateAsync<string?>("() => window.__docMarker"), Is.EqualTo("one-document"),
            "both navigations must be enhanced ones.");

        // Close the negative with a positive (see PagesThatAreNotPosts_SendNoBeacon_OnAFullLoad): a
        // full load of post B sends its beacon after any the two navigations might have sent.
        await page.GotoAsync($"{BaseUrl}/post/{_postB.Slug}");
        await WaitForInteractiveAsync(page);
        await beacons.WaitForCountAsync(2, "the beacon of the closing load of post B");
        Assert.That(beacons.Count, Is.EqualTo(2),
            "leaving a post for Home and Search is no post view: only the first beacon and the closing load's.");
        Assert.That(beacons.Bodies[1], Does.Contain(_postB.Id.ToString()), "the second beacon is the closing load of post B.");
    }

    [Test]
    public async Task TheApisViewCount_RisesByExactlyTheViews()
    {
        // Posts of this test's own: the API counts one view per (post, visitor, day) and every
        // browser of this suite comes from the same address, so a post that other tests already
        // viewed would show "1" whatever this test's reader did. Fresh posts start at 0.
        var tag = $"views{Guid.NewGuid():N}"[..20];
        var freshA = await PublishAsync($"Beacon Views A {RunId} {tag}", "Body.\n\n## One\n\nText.\n\n## Two\n\nText.", [tag]);
        var freshB = await PublishAsync($"Beacon Views B {RunId} {tag}", "Body.\n\n## One\n\nText.\n\n## Two\n\nText.", [tag]);

        await LoginAsync("admin@example.com", "password");
        foreach (var post in new[] { freshA, freshB })
        {
            await Page.GotoAsync($"{BaseUrl}/admin/posts/{post.Id}/analytics");
            await Expect(Page.GetByText("Total views: 0", new PageGetByTextOptions { Exact = true })).ToBeVisibleAsync();
        }

        // One anonymous reader: a full load of A, then an enhanced navigation to B.
        await using (var anonymous = await NewDesktopContextAsync())
        {
            var reader = await anonymous.NewPageAsync();
            // Each beacon's response is awaited before the context closes (a closing context could
            // cancel a beacon still in flight, and then the API count would not be the views).
            var firstBeacon = reader.WaitForResponseAsync(r =>
                r.Url.EndsWith("/api/analytics/pageview") && r.Request.Method == "POST");
            await reader.GotoAsync($"{BaseUrl}/post/{freshA.Slug}");
            Assert.That((await firstBeacon).Status, Is.EqualTo(204), "the first view's beacon was accepted.");
            await WaitForInteractiveAsync(reader);
            var secondBeacon = reader.WaitForResponseAsync(r =>
                r.Url.EndsWith("/api/analytics/pageview") && r.Request.Method == "POST");
            await reader.Locator($"a.related-posts__link[href='/post/{freshB.Slug}']").ClickAsync();
            await reader.WaitForURLAsync(url => url.EndsWith($"/post/{freshB.Slug}"));
            Assert.That((await secondBeacon).Status, Is.EqualTo(204), "the enhanced navigation's beacon was accepted.");
        }

        foreach (var post in new[] { freshA, freshB })
        {
            await Page.GotoAsync($"{BaseUrl}/admin/posts/{post.Id}/analytics");
            await Expect(Page.GetByText("Total views: 1", new PageGetByTextOptions { Exact = true })).ToBeVisibleAsync();
        }
    }

    private static BeaconLog CountBeacons(IPage page)
    {
        var log = new BeaconLog();
        page.Request += (_, request) =>
        {
            if (request.Method == "POST" && request.Url.EndsWith("/api/analytics/pageview"))
            {
                log.Add(request.PostData ?? string.Empty);
            }
        };
        return log;
    }

    /// <summary>The page-view beacons a page sent (their JSON bodies).</summary>
    private sealed class BeaconLog
    {
        private readonly List<string> _bodies = [];

        public int Count
        {
            get { lock (_bodies) { return _bodies.Count; } }
        }

        public IReadOnlyList<string> Bodies
        {
            get { lock (_bodies) { return [.. _bodies]; } }
        }

        public void Add(string body)
        {
            lock (_bodies)
            {
                _bodies.Add(body);
            }

        }

        /// <summary>
        /// Waits, bounded, until at least <paramref name="count"/> beacons have been seen. The count
        /// is filled by the browser's request events, which can arrive after the page-side signal a
        /// test last waited for, so a read taken at once is a race (a missing beacon looks like none
        /// yet): every assertion on the count waits for the beacon it expects first.
        /// </summary>
        public async Task WaitForCountAsync(int count, string what)
        {
            // A bounded poll of one concrete condition (the beacon count), not a pause.
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (Count < count)
            {
                if (DateTime.UtcNow > deadline)
                {
                    Assert.Fail($"Waited 15 s for {what}: expected {count} beacon(s), saw {Count}.");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(25));
            }
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

            var category = await CreateCategoryAsync($"Beacon Cat {RunId}");
            _categorySlug = category.Slug;
            _tag = $"beacon{RunId}";
            string[] tags = [_tag];
            _titleB = $"Beacon Post B {RunId}";
            _postA = await PublishAsync($"Beacon Post A {RunId}", $"Body A {RunId}.\n\n## One\n\nText.\n\n## Two\n\nText.", tags, category.Id);
            _postB = await PublishAsync(_titleB, $"Body B {RunId}.\n\n## One\n\nText.\n\n## Two\n\nText.", tags, category.Id);
            _pageSlug = (await PublishAsync($"Beacon About {RunId}", "A page.", kind: Croft.Shared.Enums.PostKind.Page)).Slug;
            _dataReady = true;
        }
        finally
        {
            DataGate.Release();
        }
    }
}
