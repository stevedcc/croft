using System.Text.RegularExpressions;

namespace Croft.Web.E2E;

/// <summary>
/// Brief 07g item 2's page editor end to end: "New page" (the admin sidebar's entry below
/// "New post") opens the editor in page mode — no category, tags or banner fields, "Show in"
/// and "Order (lowest first)" instead — and the created page publishes and lives at /{slug}.
/// Placing it in the sidebar puts it in a signed-out reader's sidebar and placing it nowhere takes
/// it away; unpublishing answers 404 at the slug; deleting removes it from the admin Pages list and
/// the public site; the Pages list shows the placement and order saved in the editor; and
/// scheduling round-trips its Scheduled status like it does for posts.
/// Each test creates its own run-keyed page through the admin UI (this item IS the editor).
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class PageWritingTests : WebTest
{
    [Test]
    public async Task AdminCan_WriteAndPublishAPage_ItLivesAtItsSlug()
    {
        await LoginAsync("admin@example.com", "password");
        var marker = Guid.NewGuid().ToString("N")[..12];
        var title = $"About {marker}";

        await OpenNewPageAsync();
        await FillTitleAndBodyAsync(title, $"## Hello\n\nPagemarker {marker} waits in the reading column.");
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Publish now" }).ClickAsync();
        await WaitForEditorAsync();
        var slug = await SlugForPostAsync(PostIdFromEditorUrl());

        await Page.GotoAsync($"{BaseUrl}/{slug}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.Locator(".page-surface__title")).ToBeVisibleAsync();

        Assert.That((await Page.Locator(".page-surface__title").TextContentAsync())?.Trim(), Is.EqualTo(title),
            "the published page loads at /<slug> with its title");
        Assert.That(await Page.Locator(".markdown-body").TextContentAsync(), Does.Contain($"Pagemarker {marker}"),
            "the Markdown body renders at /<slug>");
    }

    [Test]
    public async Task PageEditor_HidesCategoryTagsAndBanner_AndShowsTheNavigationFields()
    {
        await LoginAsync("admin@example.com", "password");
        await OpenNewPageAsync();

        await AssertPageModeFieldsAsync("on the new-page route");

        // The mode survives the create: the edit route it lands on reads the page's Kind from the
        // loaded post and keeps the page fields.
        var title = $"Modes {Guid.NewGuid():N}";
        await FillTitleAndBodyAsync(title, "Body for the mode check.");
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save draft" }).ClickAsync();
        await WaitForEditorAsync();

        await AssertPageModeFieldsAsync("on the edit route of the created page");
    }

    [Test]
    public async Task MarkedForNavigation_AppearsInTheSidebarForSignedOutReaders_UntilUnmarked()
    {
        await LoginAsync("admin@example.com", "password");
        var marker = Guid.NewGuid().ToString("N")[..12];
        var title = $"Now {marker}";

        await OpenNewPageAsync();
        await FillTitleAndBodyAsync(title, $"Navmarker {marker}.");
        await Page.GetByLabel("Show in", new() { Exact = true }).SelectOptionAsync("Sidebar");
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Publish now" }).ClickAsync();
        await WaitForEditorAsync();
        var slug = await SlugForPostAsync(PostIdFromEditorUrl());

        await using var context = await Browser.NewContextAsync(
            new BrowserNewContextOptions { ViewportSize = new() { Width = 1440, Height = 900 } });
        var reader = await context.NewPageAsync();
        await reader.GotoAsync(BaseUrl);
        await reader.WaitForLoadStateAsync(LoadState.NetworkIdle);
        var link = reader.Locator($"#sidebar-nav a[href='/{slug}']");
        // The marked page is in a signed-out reader's sidebar (the outcome for readers).
        await Expect(link).ToBeVisibleAsync();
        Assert.That((await link.Locator(".label:not(.count)").TextContentAsync())?.Trim(), Is.EqualTo(title),
            "the sidebar entry carries the page's title");

        // Unmark and save. The save of an existing post travels over the circuit with no browser
        // response to await (LongPostEditorTests' rule), so wait on the outcome: the server-
        // rendered home page stops carrying the sidebar link.
        await Page.GetByLabel("Show in", new() { Exact = true }).SelectOptionAsync("None");
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save draft" }).ClickAsync();
        await reader.WaitForFunctionAsync(
            """
            slug => {
                const request = new XMLHttpRequest();
                request.open('GET', '/', false);
                request.send();
                return !request.responseText.includes('href="/' + slug + '"');
            }
            """,
            slug,
            new PageWaitForFunctionOptions { Timeout = 30000 });

        await reader.ReloadAsync();
        await reader.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(reader.Locator("#sidebar-nav a[href='/login']")).ToBeVisibleAsync();
        // The unmarked page leaves the sidebar.
        await Expect(link).ToHaveCountAsync(0);
    }

    [Test]
    public async Task UnpublishingAPage_MakesItsSlugAnswer404ForReaders()
    {
        await LoginAsync("admin@example.com", "password");
        var marker = Guid.NewGuid().ToString("N")[..12];
        var slug = await CreateAndPublishPageAsync($"Gone {marker}", $"Vanishmarker {marker}.");

        // The reader exists before the unpublish, so the outcome can be awaited where it matters:
        // a signed-out request (a signed-in editor still sees draft pages at /<slug>).
        await using var context = await Browser.NewContextAsync(
            new BrowserNewContextOptions { ViewportSize = new() { Width = 1440, Height = 900 } });
        var reader = await context.NewPageAsync();
        await reader.GotoAsync(BaseUrl);
        await reader.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await using var published = await context.APIRequest.GetAsync($"{BaseUrl}/{slug}");
        Assert.That(published.Status, Is.EqualTo(200), "the published page is served before the unpublish");

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Unpublish" }).ClickAsync();

        // No browser response to await: wait on the reader's outcome (the slug answers 404).
        await reader.WaitForFunctionAsync(
            """
            slug => {
                const request = new XMLHttpRequest();
                request.open('GET', '/' + slug, false);
                request.send();
                return request.status === 404;
            }
            """,
            slug,
            new PageWaitForFunctionOptions { Timeout = 30000 });

        await using var response = await context.APIRequest.GetAsync($"{BaseUrl}/{slug}");
        Assert.That(response.Status, Is.EqualTo(404),
            "an unpublished page must answer HTTP 404 at its slug, not a redirect");
        Assert.That(await response.TextAsync(), Does.Contain("This page has wandered off"),
            "the 404 response renders the not-found copy (spec §5.5)");
    }

    [Test]
    public async Task DeletingAPage_RemovesItFromTheAdminListAndThePublicSite()
    {
        await LoginAsync("admin@example.com", "password");
        var marker = Guid.NewGuid().ToString("N")[..12];
        var title = $"Scrap {marker}";
        var slug = await CreateAndPublishPageAsync(title, $"Deletemarker {marker}.");

        await Page.GotoAsync($"{BaseUrl}/admin/pages");
        await WaitForInteractiveAsync(Page);
        await Page.GetByLabel("Search titles").FillAsync(title);
        await Page.GetByLabel("Search titles").PressAsync("Enter");
        var rows = Page.Locator(".posts-grid__row");
        var row = rows.Filter(new LocatorFilterOptions { HasText = title });
        // The searched grid holds exactly one row; only after it lands is the Delete click on the
        // searched row — before that the unfiltered first page of rows is still on screen.
        await Expect(rows).ToHaveCountAsync(1);
        await Expect(row).ToBeVisibleAsync();

        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = $"Delete {title}", Exact = true }).ClickAsync();
        var dialog = Page.Locator(".mud-dialog");
        await Expect(dialog).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15000 });
        await dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Delete", Exact = true }).ClickAsync();

        // The grid reloads only after the delete returns, so the row's disappearance is the
        // outcome the owner cares about — and the public slug must go with it.
        await Expect(row).ToHaveCountAsync(0);
        await using var response = await Page.APIRequest.GetAsync($"{BaseUrl}/{slug}");
        Assert.That(response.Status, Is.EqualTo(404),
            "a deleted page must stop being served at its slug");
    }

    [Test]
    public async Task ANewPage_AfterTheFirstSave_IsEditedUnderAdminPages_WithPagesHighlighted()
    {
        // Brief 10f item 3: a page lives under /admin/pages in the address bar and the sidebar.
        await LoginAsync("admin@example.com", "password");
        await OpenNewPageAsync();
        await FillTitleAndBodyAsync($"Route {Guid.NewGuid():N}", "Body for the route check.");
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save draft" }).ClickAsync();
        await WaitForEditorAsync();

        Assert.That(Page.Url, Does.StartWith($"{BaseUrl}/admin/pages/").And.EndsWith("/edit"));
        await Expect(Page.Locator("#sidebar-nav a.nav-item[href='/admin/pages']")).ToHaveClassAsync(new Regex("\\bactive\\b"));
        await Expect(Page.Locator("#sidebar-nav a.nav-item[href='/admin/posts']")).Not.ToHaveClassAsync(new Regex("\\bactive\\b"));
    }

    [Test]
    public async Task ANewPost_AfterTheFirstSave_StaysUnderAdminPosts_WithPostsHighlighted()
    {
        await LoginAsync("admin@example.com", "password");
        await Page.GotoAsync($"{BaseUrl}/admin/posts/new");
        await WaitForInteractiveAsync(Page);
        await FillTitleAndBodyAsync($"Postroute {Guid.NewGuid():N}", "Body for the route check.");
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save draft" }).ClickAsync();
        await Page.WaitForURLAsync(url => url.StartsWith($"{BaseUrl}/admin/posts/") && url.EndsWith("/edit"),
            new PageWaitForURLOptions { Timeout = 15000 });

        // The active-state class follows the URL a moment after it changes, so wait on the link.
        await Expect(Page.Locator("#sidebar-nav a.nav-item[href='/admin/posts']")).ToHaveClassAsync(new Regex("\\bactive\\b"));
        await Expect(Page.Locator("#sidebar-nav a.nav-item[href='/admin/pages']")).Not.ToHaveClassAsync(new Regex("\\bactive\\b"));
    }

    [Test]
    public async Task ANewPage_SavedWithAPlacement_IsListedUnderPagesWithThatPlacementAndOrder()
    {
        // Brief 10f item 3. The editor does not navigate to a list after the first save: it stays
        // on the page's edit route (the same route for both kinds), so the outcome checked is the
        // one the owner cares about — the saved placement and order show in the Pages list.
        await LoginAsync("admin@example.com", "password");
        var marker = Guid.NewGuid().ToString("N")[..12];
        var title = $"Placed {marker}";

        await OpenNewPageAsync();
        await FillTitleAndBodyAsync(title, $"Placedmarker {marker}.");
        await Page.GetByLabel("Show in", new PageGetByLabelOptions { Exact = true }).SelectOptionAsync("Footer");
        await Page.GetByLabel("Order (lowest first)").FillAsync("7");
        await Page.GetByLabel("Order (lowest first)").BlurAsync();
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save draft" }).ClickAsync();
        await WaitForEditorAsync();

        await Page.GotoAsync($"{BaseUrl}/admin/pages");
        await WaitForInteractiveAsync(Page);
        await Page.GetByLabel("Search titles").FillAsync(marker);
        await Page.GetByLabel("Search titles").PressAsync("Enter");

        var row = Page.Locator(".posts-grid__row").Filter(new LocatorFilterOptions { HasText = title });
        await Expect(row).ToHaveCountAsync(1);
        Assert.That((await row.Locator(".posts-grid__placement").TextContentAsync())?.Trim(), Is.EqualTo("Footer"),
            "the placement chosen in the editor is saved");
        Assert.That((await row.Locator(".posts-grid__order").TextContentAsync())?.Trim(), Is.EqualTo("7"),
            "the order typed in the editor is saved");
    }

    [Test]
    public async Task SchedulingAPage_RoundTripsItsScheduledStatus()
    {
        await LoginAsync("admin@example.com", "password");
        var marker = Guid.NewGuid().ToString("N")[..12];
        var title = $"Later {marker}";

        await OpenNewPageAsync();
        await FillTitleAndBodyAsync(title, $"Schedulemarker {marker}.");

        // Fixed absolute target (SchedulingTests' rule): no relative drift into the wrong year.
        await PickUtcDateAsync(day: "15", monthYear: "June 2030");
        await PickUtcTimeAsync(hour: 15, minute: 30);
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Schedule" }).ClickAsync();
        await WaitForEditorAsync();

        // The API's Scheduled status is the one shown after scheduling.
        await Expect(Page.Locator(".status-badge")).ToHaveTextAsync("Scheduled");
        var editUrl = Page.Url;

        // The scheduled moment round-trips through the API: a fresh load of the editor shows the
        // entered UTC moment again and keeps the status.
        await Page.GotoAsync(editUrl);
        await WaitForInteractiveAsync(Page);
        await Expect(Page.GetByLabel("Publish date (UTC)")).ToHaveValueAsync("2030-06-15");
        await Expect(Page.GetByLabel("Publish time (UTC)")).ToHaveValueAsync("15:30");
        await Expect(Page.Locator(".status-badge")).ToHaveTextAsync("Scheduled");

        // An update on the scheduled page keeps the schedule: every update carries the page's
        // existing Kind (a Kind change is the API's 400), so the save must succeed and survive.
        await Page.GetByLabel("Markdown").FillAsync($"Schedulemarker {marker} edited.");
        await Page.GetByLabel("Markdown").BlurAsync();
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save draft" }).ClickAsync();
        await Page.WaitForFunctionAsync(
            """
            marker => {
                const request = new XMLHttpRequest();
                request.open('GET', location.pathname, false);
                request.send();
                return request.responseText.includes('Schedulemarker ' + marker + ' edited.');
            }
            """,
            marker,
            new PageWaitForFunctionOptions { Timeout = 30000 });

        await Page.GotoAsync(editUrl);
        await WaitForInteractiveAsync(Page);
        // Editing the body must not disturb the schedule.
        await Expect(Page.Locator(".status-badge")).ToHaveTextAsync("Scheduled");
        Assert.That(await Page.GetByLabel("Markdown").InputValueAsync(), Does.Contain("edited."),
            "the edited body persisted through the update");
    }

    /// <summary>The page editor's page-mode shape: the post-only fields are gone and the
    /// navigation fields are there.</summary>
    private async Task AssertPageModeFieldsAsync(string where)
    {
        await Expect(Page.GetByLabel("Category")).ToHaveCountAsync(0);
        await Expect(Page.GetByLabel("Tags")).ToHaveCountAsync(0);
        await Expect(Page.GetByLabel("Upload banner")).ToHaveCountAsync(0);
        await Expect(Page.GetByLabel("Show in", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(Page.GetByLabel("Order (lowest first)")).ToBeVisibleAsync();
        TestContext.Progress.WriteLine($"Page mode fields checked {where}.");
    }

    private async Task OpenNewPageAsync()
    {
        await Page.GotoAsync($"{BaseUrl}/admin/pages/new");
        await WaitForInteractiveAsync(Page);
        await Expect(Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Publish now" }))
            .ToBeVisibleAsync();
    }

    private async Task FillTitleAndBodyAsync(string title, string markdown)
    {
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync(title);
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).BlurAsync();
        await Page.GetByLabel("Markdown").FillAsync(markdown);
        await Page.GetByLabel("Markdown").BlurAsync();
    }

    /// <summary>Creates and publishes a page through the editor; returns its slug.</summary>
    private async Task<string> CreateAndPublishPageAsync(string title, string markdown)
    {
        await OpenNewPageAsync();
        await FillTitleAndBodyAsync(title, markdown);
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Publish now" }).ClickAsync();
        await WaitForEditorAsync();
        return await SlugForPostAsync(PostIdFromEditorUrl());
    }

    private async Task WaitForEditorAsync()
    {
        try
        {
            await Page.WaitForURLAsync(
                url => url.StartsWith($"{BaseUrl}/admin/pages/") && url.EndsWith("/edit"),
                new PageWaitForURLOptions { Timeout = 15000 });
        }
        catch (TimeoutException)
        {
            var url = Page.Url;
            var html = await Page.ContentAsync();
            throw new TimeoutException(
                $"The editor did not navigate to /admin/pages/{{id}}/edit. Current URL: {url}. Page content:\n{html}");
        }

        await WaitForInteractiveAsync(Page);
    }

    /// <summary>Sets the publish date through the picker popup (SchedulingTests' dance): navigate
    /// the calendar to the target month/year, then click the day.</summary>
    private async Task PickUtcDateAsync(string day, string monthYear)
    {
        await Page.GetByLabel("Publish date (UTC)").ClickAsync();
        var pickerOverlay = Page.Locator(".mud-picker-open, .mud-popover-open").First;
        await pickerOverlay.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5000 });

        var monthHeader = pickerOverlay.Locator(".mud-picker-calendar-header p.mud-typography-align-center");
        var nextBtn = pickerOverlay.Locator(".mud-picker-calendar-header button:last-child");
        for (var safety = 0; safety < 120; safety++)
        {
            var headerText = (await monthHeader.TextContentAsync() ?? string.Empty).Trim();
            if (headerText.Contains(monthYear, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            var before = headerText;
            await nextBtn.ClickAsync();
            await Page.WaitForFunctionAsync(
                @"(args) => {
                    const el = document.querySelector('.mud-picker-calendar-header p.mud-typography-align-center');
                    return el && !el.textContent.includes(args.before);
                }",
                new { before },
                new PageWaitForFunctionOptions { Timeout = 3000 });
        }

        var finalHeader = (await monthHeader.TextContentAsync() ?? string.Empty).Trim();
        Assert.That(finalHeader, Does.Contain(monthYear),
            $"Calendar header should show {monthYear} but showed: {finalHeader}");

        var dayButtons = pickerOverlay.Locator($".mud-picker-calendar-day:has-text('{day}')");
        await dayButtons.Last.ClickAsync();
        await Page.Locator(".mud-picker-open, .mud-popover-open").First.WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Detached, Timeout = 5000 });
    }

    // WORKAROUND(W-073, MudBlazor): the clock is a pointer-event mask placed on a requestAnimationFrame; clicks use element-relative points
    /// <summary>Sets the publish time through the clock picker (SchedulingTests' dance): hour on
    /// the outer ring at its clock angle, then the same for the minute. The ring points are
    /// clicked as element-relative points on the clock mask, never as raw page coordinates:
    /// the popover is placed on a requestAnimationFrame after its open class lands, so a
    /// bounding box read as soon as the picker is "visible" can still be the unplaced
    /// (-9999px) one and send a raw click nowhere. A locator click waits for the mask to be
    /// placed and stable and derives the point from its box at click time.</summary>
    private async Task PickUtcTimeAsync(int hour, int minute)
    {
        await Page.GetByLabel("Publish time (UTC)").ClickAsync();
        var timePicker = Page.Locator(".mud-picker-open, .mud-popover-open").First;
        await timePicker.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5000 });

        var clockMask = timePicker.Locator(".mud-picker-time-clock-mask");
        var box = await clockMask.BoundingBoxAsync();
        Assert.That(box, Is.Not.Null, "Clock mask bounding box not found");

        var width = (double)box.Width;
        var height = (double)box.Height;

        // 12 hours sit around the dial; the ring is ~75% of the radius (SchedulingTests' probe).
        var hourAngle = (hour % 12) * 30 * Math.PI / 180;
        await clockMask.ClickAsync(new LocatorClickOptions
        {
            Position = new()
            {
                X = (float)(width / 2 + width / 2 * 0.75 * Math.Sin(hourAngle)),
                Y = (float)(height / 2 - width / 2 * 0.75 * Math.Cos(hourAngle)),
            },
        });
        await timePicker.Locator(".mud-time-picker-minute:not(.mud-time-picker-dial-hidden)").First.WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5000 });

        var minuteAngle = (minute % 60) * 6 * Math.PI / 180;
        await clockMask.ClickAsync(new LocatorClickOptions
        {
            Position = new()
            {
                X = (float)(width / 2 + width / 2 * 0.75 * Math.Sin(minuteAngle)),
                Y = (float)(height / 2 - width / 2 * 0.75 * Math.Cos(minuteAngle)),
            },
        });
        await Page.Locator(".mud-picker-open, .mud-popover-open").First.WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Detached, Timeout = 5000 });
    }
    // END WORKAROUND(W-073)
}
