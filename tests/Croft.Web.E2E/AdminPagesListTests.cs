using Croft.Shared.Enums;

namespace Croft.Web.E2E;

/// <summary>
/// Brief 10f item 3: Admin → Pages lists the pages (placement, order, status, updated) sorted
/// Sidebar, Footer, then nowhere, each by order and title, and no posts; Admin → Posts lists the
/// posts and no pages. The fixtures are published through the API; the outcomes are the rows the
/// owner sees in the browser.
/// Break-it-once (recorded in the commit): the Kind filter removed from the posts list; the posts
/// test fails because the page's row shows up under Posts.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class AdminPagesListTests : StaticReadingTest
{
    private static readonly SemaphoreSlim DataGate = new(1, 1);
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];
    private static bool _dataReady;

    private static string SidebarTitle => $"Aplist Side {RunId}";
    private static string FooterLateTitle => $"Aplist Footlate {RunId}";
    private static string FooterEarlyTitle => $"Aplist Footearly {RunId}";
    private static string NowhereTitle => $"Aplist Nowhere {RunId}";
    private static string PostTitle => $"Aplist Post {RunId}";

    [Test]
    public async Task AdminPages_ListsPagesWithPlacementAndOrder_SortedByPlacementThenOrder_AndNoPosts()
    {
        await EnsureFixtureAsync();
        await LoginAsync("admin@example.com", "password");
        await OpenListAsync("/admin/pages");

        var rows = Page.Locator(".posts-grid__row");
        var bound = new LocatorAssertionsToHaveCountOptions { Timeout = SlowLoadTimeoutMs };

        // Every read below polls (an Expect, not a one-shot read): the grid loads twice after the
        // circuit connects (its first interactive load and the search reload), and a read taken
        // while the superseded load is still rendering sees rows mid-patch. Each wait names itself.
        await WaitedFor($"the Pages grid to list the 4 pages of this run after the search for '{RunId}'",
            () => Expect(rows).ToHaveCountAsync(4, bound));
        await WaitedFor("the Pages grid rows to be sorted Sidebar, then Footer by order, then nowhere",
            () => Expect(rows.Locator(".posts-grid__title")).ToHaveTextAsync(
                [SidebarTitle, FooterEarlyTitle, FooterLateTitle, NowhereTitle],
                new LocatorAssertionsToHaveTextOptions { Timeout = SlowLoadTimeoutMs }));

        async Task AssertRowAsync(string title, string placement, string order)
        {
            var row = rows.Filter(new LocatorFilterOptions { HasText = title });
            var options = new LocatorAssertionsToHaveTextOptions { Timeout = SlowLoadTimeoutMs };
            await WaitedFor($"the row '{title}' to show placement '{placement}'",
                () => Expect(row.Locator(".posts-grid__placement")).ToHaveTextAsync(placement, options));
            await WaitedFor($"the row '{title}' to show order '{order}'",
                () => Expect(row.Locator(".posts-grid__order")).ToHaveTextAsync(order, options));
        }

        await AssertRowAsync(SidebarTitle, "Sidebar", "1");
        await AssertRowAsync(FooterEarlyTitle, "Footer", "1");
        await AssertRowAsync(FooterLateTitle, "Footer", "4");
        await AssertRowAsync(NowhereTitle, "–", "0");
        await WaitedFor("the Pages list to hold no post of this run",
            () => Expect(rows.Filter(new LocatorFilterOptions { HasText = PostTitle })).ToHaveCountAsync(0, bound));
        await Expect(Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Analytics" })).ToHaveCountAsync(0);

        await Page.Locator(".posts-grid__row").First.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Edit" }).ClickAsync();
        await WaitedFor("the browser to reach the page editor after clicking Edit on the first row",
            () => Page.WaitForURLAsync(
                url => url.StartsWith($"{BaseUrl}/admin/pages/") && url.EndsWith("/edit"),
                new PageWaitForURLOptions { Timeout = SlowLoadTimeoutMs }));
        await WaitedFor("the 'Edit Page' heading of the page editor (the editor loads the page, its tags and its feedback)",
            () => Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Edit Page" }))
                .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = SlowLoadTimeoutMs }));
    }

    [Test]
    public async Task AdminPosts_ListsPosts_AndNoPages()
    {
        await EnsureFixtureAsync();
        await LoginAsync("admin@example.com", "password");
        await OpenListAsync("/admin/posts");

        var rows = Page.Locator(".posts-grid__row");
        await Expect(rows).ToHaveCountAsync(1);
        await Expect(rows.Filter(new LocatorFilterOptions { HasText = PostTitle })).ToHaveCountAsync(1);
        Assert.That(await rows.Filter(new LocatorFilterOptions { HasText = "Aplist" }).CountAsync(), Is.EqualTo(1),
            "only the post of this run's fixtures is listed; its four pages are not");
    }

    [Test]
    public async Task TheAdminSidebar_HasAPagesEntryNextToPosts_ThatOpensThePagesList()
    {
        await LoginAsync("admin@example.com", "password");
        await Page.GotoAsync($"{BaseUrl}/admin/posts");
        await WaitForInteractiveAsync(Page);

        await Page.Locator("#sidebar-nav").GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = "Pages", Exact = true }).ClickAsync();

        await Page.WaitForURLAsync($"{BaseUrl}/admin/pages");
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Pages", Exact = true })).ToBeVisibleAsync();
    }

    private async Task OpenListAsync(string path)
    {
        await Page.GotoAsync($"{BaseUrl}{path}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await WaitForInteractiveAsync(Page);
        await Page.GetByLabel("Search titles").FillAsync(RunId);
        await Page.GetByLabel("Search titles").PressAsync("Enter");
    }

    private async Task EnsureFixtureAsync()
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

            await PublishAsync(PostTitle, "A post, so the Pages list has something to leave out.");
            await PublishAsync(NowhereTitle, "Shown nowhere.", kind: PostKind.Page);
            await PublishAsync(FooterLateTitle, "Footer, order 4.", kind: PostKind.Page,
                placement: NavigationPlacement.Footer, navigationOrder: 4);
            await PublishAsync(FooterEarlyTitle, "Footer, order 1.", kind: PostKind.Page,
                placement: NavigationPlacement.Footer, navigationOrder: 1);
            await PublishAsync(SidebarTitle, "Sidebar, order 1.", kind: PostKind.Page,
                placement: NavigationPlacement.Sidebar, navigationOrder: 1);
            _dataReady = true;
        }
        finally
        {
            DataGate.Release();
        }
    }
}
