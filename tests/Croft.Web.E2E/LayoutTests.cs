using System.Text.Json;

namespace Croft.Web.E2E;

/// <summary>
/// Task R2.2 Step 1: the layout shell at the three breakpoints (spec §4.1). The default-state
/// tests run with JavaScript disabled — "correct with JS disabled" is the requirement. Playwright
/// locator queries (BoundingBoxAsync, IsVisibleAsync) were probed to work with JS disabled, so the
/// layout is measured directly; the ☰/Collapse behaviour tests run with JS on.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class LayoutTests : WebTest
{
    private static readonly SemaphoreSlim DataGate = new(1, 1);
    private static bool _dataReady;

    [Test]
    public async Task NoJs_FullWidth_SidebarIs260Wide_AndCategoryLabelVisible()
    {
        await EnsureSidebarDataAsync();
        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            JavaScriptEnabled = false,
            ViewportSize = new() { Width = 1440, Height = 900 }
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync(BaseUrl);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var sidebar = page.Locator("#sidebar-nav");
        await sidebar.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });
        var box = await sidebar.BoundingBoxAsync();
        Assert.That(box, Is.Not.Null, "The sidebar has no bounding box at 1440x900.");
        Assert.That(box!.Width, Is.EqualTo(260).Within(1), $"Sidebar width at 1440x900 is {box.Width}, expected 260.");

        var label = page.Locator("#sidebar-nav a.nav-item[href^='/category/'] .label:not(.count)").First;
        Assert.That(await label.IsVisibleAsync(), Is.True, "A category label is not visible at 1440x900.");
    }

    [Test]
    public async Task NoJs_RailWidth_SidebarIs64Wide_LabelsHidden_LogoVisible()
    {
        await EnsureSidebarDataAsync();
        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            JavaScriptEnabled = false,
            ViewportSize = new() { Width = 1100, Height = 800 }
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync(BaseUrl);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var sidebar = page.Locator("#sidebar-nav");
        await sidebar.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });
        var box = await sidebar.BoundingBoxAsync();
        Assert.That(box, Is.Not.Null, "The sidebar has no bounding box at 1100x800.");
        Assert.That(box!.Width, Is.EqualTo(64).Within(1), $"Sidebar width at 1100x800 is {box.Width}, expected the 64px rail.");

        var label = page.Locator("#sidebar-nav a.nav-item[href^='/category/'] .label:not(.count)").First;
        Assert.That(await label.IsVisibleAsync(), Is.False, "Category labels must be hidden in the 960-1279px rail.");

        var logo = page.Locator("#sidebar-nav .brand img");
        Assert.That(await logo.IsVisibleAsync(), Is.True, "The logo must stay visible in the rail.");
    }

    [Test]
    public async Task NoJs_PhoneWidth_SidebarOffscreen_TopBarWithMenuVisible()
    {
        await EnsureSidebarDataAsync();
        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            JavaScriptEnabled = false,
            ViewportSize = new() { Width = 390, Height = 844 }
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync(BaseUrl);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var sidebar = page.Locator("#sidebar-nav");
        var box = await sidebar.BoundingBoxAsync();
        Assert.That(box, Is.Not.Null, "The sidebar has no bounding box at 390x844.");
        Assert.That(box!.X + box.Width, Is.LessThanOrEqualTo(0),
            $"The sidebar is inside the viewport at 390x844: x={box.X}, width={box.Width}, x+width={box.X + box.Width}.");

        Assert.That(await page.Locator(".topbar").IsVisibleAsync(), Is.True, "The phone top bar is not visible at 390x844.");
        Assert.That(await page.Locator(".topbar").GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Menu" }).IsVisibleAsync(), Is.True,
            "The top bar's ☰ (Menu) button is not visible at 390x844.");
    }

    /// <summary>
    /// Final review finding 1: the closed phone overlay is offscreen but was still visible to the
    /// accessibility tree, so its links stayed in the tab order (~10 invisible tab stops before
    /// <c>main</c>, with and without JS). Closed must mean not visible; the layout geometry is
    /// unaffected because <c>visibility</c> does not remove layout.
    /// </summary>
    [Test]
    public async Task NoJs_PhoneWidth_ClosedSidebarLinksNotVisible_GeometryStillOffscreen()
    {
        await EnsureSidebarDataAsync();
        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            JavaScriptEnabled = false,
            ViewportSize = new() { Width = 390, Height = 844 }
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync(BaseUrl);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var sidebar = page.Locator("#sidebar-nav");
        var box = await sidebar.BoundingBoxAsync();
        Assert.That(box, Is.Not.Null, "The sidebar has no bounding box at 390x844.");
        Assert.That(box!.X + box.Width, Is.LessThanOrEqualTo(0),
            $"The sidebar is inside the viewport at 390x844: x={box.X}, width={box.Width}, x+width={box.X + box.Width}.");

        var label = page.Locator("#sidebar-nav a.nav-item[href^='/category/'] .label:not(.count)").First;
        Assert.That(await label.IsVisibleAsync(), Is.False,
            "The closed sidebar's category label is visible at 390x844 — its links stay in the tab order.");
    }

    /// <summary>
    /// Final review finding 1's other half: hiding the closed overlay must not hide the sidebar
    /// when it is open — the same links become visible again.
    /// </summary>
    [Test]
    public async Task Phone_OpenSidebar_ClosedHiddenLinksBecomeVisible()
    {
        await EnsureSidebarDataAsync();
        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new() { Width = 390, Height = 844 }
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync(BaseUrl);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var label = page.Locator("#sidebar-nav a.nav-item[href^='/category/'] .label:not(.count)").First;
        Assert.That(await label.IsVisibleAsync(), Is.False,
            "The closed sidebar's category label is visible at 390x844.");

        await page.Locator(".topbar").GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Menu" }).ClickAsync();
        await page.WaitForFunctionAsync(
            "() => { const el = document.querySelector('#sidebar-nav'); return el && Math.abs(el.getBoundingClientRect().x) < 1; }");

        Assert.That(await label.IsVisibleAsync(), Is.True,
            "The sidebar's category label is still hidden after opening the overlay at 390x844.");
    }

    /// <summary>
    /// Final review finding 2: <c>blog-sidebar=collapsed</c> can only be stored at ≥1280px, but the
    /// stored state survives a narrower window. The collapsed-label rule must not leak into the
    /// phone overlay: labels, section headings and the brand name stay visible there.
    /// </summary>
    [Test]
    public async Task Phone_StoredCollapsed_OpenOverlay_ShowsLabelsHeadingsAndBrandName()
    {
        await EnsureSidebarDataAsync();
        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new() { Width = 390, Height = 844 }
        });
        await context.AddInitScriptAsync("localStorage.setItem('blog-sidebar', 'collapsed')");
        var page = await context.NewPageAsync();
        await page.GotoAsync(BaseUrl);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var collapsedApplied = await page.EvaluateAsync<bool>(
            "() => document.documentElement.classList.contains('sidebar-collapsed')");
        Assert.That(collapsedApplied, Is.True, "The stored blog-sidebar=collapsed state was not applied at load.");

        await page.Locator(".topbar").GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Menu" }).ClickAsync();
        await page.WaitForFunctionAsync(
            "() => { const el = document.querySelector('#sidebar-nav'); return el && Math.abs(el.getBoundingClientRect().x) < 1; }");

        var label = page.Locator("#sidebar-nav a.nav-item[href^='/category/'] .label:not(.count)").First;
        Assert.That(await label.IsVisibleAsync(), Is.True,
            "The overlay's category label is hidden at 390px with blog-sidebar=collapsed stored.");

        var heading = page.Locator("#sidebar-nav").GetByRole(AriaRole.Group, new LocatorGetByRoleOptions { Name = "Categories" }).Locator("xpath=preceding-sibling::*[1]"); // a labelling div since brief 13g item 5 (was an h2)
        Assert.That(await heading.IsVisibleAsync(), Is.True,
            "The overlay's Categories section heading is hidden at 390px with blog-sidebar=collapsed stored.");

        var brandName = page.Locator("#sidebar-nav .brand-name");
        Assert.That(await brandName.IsVisibleAsync(), Is.True,
            "The overlay's brand name is hidden at 390px with blog-sidebar=collapsed stored.");
    }

    [Test]
    public async Task Phone_MenuButton_OpensSidebar_CategoryClickNavigatesAndCloses()
    {
        await EnsureSidebarDataAsync();
        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new() { Width = 390, Height = 844 }
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync(BaseUrl);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var sidebar = page.Locator("#sidebar-nav");
        var menu = page.Locator(".topbar").GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Menu" });
        await menu.ClickAsync();
        await page.WaitForFunctionAsync(
            "() => { const el = document.querySelector('#sidebar-nav'); return el && Math.abs(el.getBoundingClientRect().x) < 1; }");

        var open = await sidebar.BoundingBoxAsync();
        Assert.That(open!.X, Is.EqualTo(0).Within(1), $"The opened sidebar is not at x=0: x={open.X}.");
        Assert.That(await menu.GetAttributeAsync("aria-expanded"), Is.EqualTo("true"),
            "aria-expanded was not true after opening the sidebar.");

        // Picking a category navigates to the category page and closes the overlay. The sidebar
        // caps at the top 12 by published-post count, so pick the category item the sidebar
        // actually renders and follow its own href.
        var (categoryHref, _) = await FirstSidebarCategoryAsync(page);
        await page.Locator($"#sidebar-nav a[href='{categoryHref}']").ClickAsync();
        await page.WaitForURLAsync(url => url.EndsWith(categoryHref), new PageWaitForURLOptions { Timeout = 15000 });
        // Fix wave 2026-10-05: the wait must settle on the same state the assertion below checks
        // ("closed = fully offscreen", x + width <= 0). The old `<= 1` wait resolved up to 1px
        // before the 0.2s close transition's end, so the assertion raced the final frame
        // (observed x=-279.99997 under load). The settled box is exactly 0; no assertion changed.
        await page.WaitForFunctionAsync(
            "() => { const el = document.querySelector('#sidebar-nav'); const r = el.getBoundingClientRect(); return r.x + r.width <= 0; }");

        var closed = await sidebar.BoundingBoxAsync();
        Assert.That(closed!.X + closed.Width, Is.LessThanOrEqualTo(0),
            $"The sidebar overlay did not close after navigating: x={closed.X}, width={closed.Width}.");
    }

    [Test]
    public async Task Phone_Esc_ClosesSidebar()
    {
        await EnsureSidebarDataAsync();
        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new() { Width = 390, Height = 844 }
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync(BaseUrl);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var sidebar = page.Locator("#sidebar-nav");
        await page.Locator(".topbar").GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Menu" }).ClickAsync();
        await page.WaitForFunctionAsync(
            "() => { const el = document.querySelector('#sidebar-nav'); return el && Math.abs(el.getBoundingClientRect().x) < 1; }");

        await page.Keyboard.PressAsync("Escape");
        // Fix wave 2026-10-05: wait for the state the assertion below checks ("closed = fully
        // offscreen", x + width <= 0) — the old `<= 1` wait resolved up to 1px before the 0.2s
        // close transition's end and the assertion raced the final frame (observed x=-279.99997
        // under load). The settled box is exactly 0; no assertion changed.
        await page.WaitForFunctionAsync(
            "() => { const el = document.querySelector('#sidebar-nav'); const r = el.getBoundingClientRect(); return r.x + r.width <= 0; }");

        var box = await sidebar.BoundingBoxAsync();
        Assert.That(box!.X + box.Width, Is.LessThanOrEqualTo(0), $"Esc did not close the sidebar overlay: x={box.X}.");
    }

    [Test]
    public async Task Phone_OpenSidebar_TrapsTabInside()
    {
        await EnsureSidebarDataAsync();
        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new() { Width = 390, Height = 844 }
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync(BaseUrl);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await page.Locator(".topbar").GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Menu" }).ClickAsync();
        await page.WaitForFunctionAsync(
            "() => { const el = document.querySelector('#sidebar-nav'); return el && Math.abs(el.getBoundingClientRect().x) < 1; }");

        for (var i = 0; i < 30; i++)
        {
            await page.Keyboard.PressAsync("Tab");
        }

        var focusInside = await page.EvaluateAsync<bool>("() => !!document.activeElement.closest('#sidebar-nav')");
        Assert.That(focusInside, Is.True, "Focus escaped the open sidebar overlay while tabbing.");
    }

    /// <summary>
    /// Final review finding 3: opening the overlay must move focus into the sidebar immediately —
    /// not leave it on the top-bar ☰ (which sits under the overlay). The old code focused the
    /// first <c>a[href], button</c>, the rail toggle, which is <c>display:none</c> below 960px.
    /// </summary>
    [Test]
    public async Task Phone_OpenSidebar_MovesFocusIntoSidebarNotTopBarMenu()
    {
        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new() { Width = 390, Height = 844 }
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync(BaseUrl);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await page.Locator(".topbar").GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Menu" }).ClickAsync();

        var focusInside = await page.EvaluateAsync<bool>("() => !!document.activeElement.closest('#sidebar-nav')");
        Assert.That(focusInside, Is.True,
            "Focus did not move into the sidebar right after opening the overlay at 390x844.");
    }

    [Test]
    public async Task FullWidth_Collapse_ShrinksTo64_AndPersistsAcrossReload()
    {
        await EnsureSidebarDataAsync();
        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new() { Width = 1440, Height = 900 }
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync(BaseUrl);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var sidebar = page.Locator("#sidebar-nav");
        await sidebar.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });
        var open = await sidebar.BoundingBoxAsync();
        Assert.That(open!.Width, Is.EqualTo(260).Within(1), $"Sidebar width before Collapse is {open.Width}, expected 260.");

        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Collapse" }).ClickAsync();
        await page.WaitForFunctionAsync(
            "() => Math.abs(document.querySelector('#sidebar-nav').getBoundingClientRect().width - 64) < 1");

        var collapsed = await sidebar.BoundingBoxAsync();
        Assert.That(collapsed!.Width, Is.EqualTo(64).Within(1), $"Sidebar width after Collapse is {collapsed.Width}, expected 64.");

        await page.ReloadAsync();
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await sidebar.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });

        var reloaded = await sidebar.BoundingBoxAsync();
        Assert.That(reloaded!.Width, Is.EqualTo(64).Within(1),
            $"The collapsed sidebar was not remembered across a reload: width is {reloaded.Width}, expected 64.");
    }

    /// <summary>
    /// Task R3.0 item 1: the sidebar's scoped CSS must land on the NavLink root element. Scoped
    /// CSS does not reach a child component's root, so before the wrapper + ::deep fix the
    /// <c>.nav-item</c> rules were dead and the active "Home" link had no <c>--primary-strong</c>
    /// background. Item 3 (pin the scoped-CSS link) is covered here: these computed styles fail if
    /// <c>Croft.Web.styles.css</c> is not linked. Measured on the &lt;a&gt; itself.
    /// </summary>
    [Test]
    public async Task NavItem_ActiveLink_HasPrimaryStrongBackgroundAndWhiteText()
    {
        await EnsureSidebarDataAsync();
        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new() { Width = 1440, Height = 900 }
        });
        await context.AddInitScriptAsync("localStorage.setItem('blog-theme', 'light')");
        var page = await context.NewPageAsync();
        await page.GotoAsync(BaseUrl);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var home = page.Locator("#sidebar-nav a.nav-item[href='/']");
        await home.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });

        var styles = await page.EvaluateAsync<string[]>(@"() => {
            const link = document.querySelector(""#sidebar-nav a.nav-item[href='/']"");
            const probe = document.createElement('div');
            probe.style.backgroundColor = 'var(--primary-strong)';
            document.body.appendChild(probe);
            const token = getComputedStyle(probe).backgroundColor;
            probe.remove();
            const cs = getComputedStyle(link);
            return [cs.backgroundColor, cs.color, token];
        }");

        Assert.That(styles[2], Is.EqualTo("rgb(154, 52, 18)"),
            $"--primary-strong resolved to {styles[2]}, expected rgb(154, 52, 18) in light mode (branding.example's primary-strong).");
        Assert.That(styles[0], Is.EqualTo(styles[2]),
            $"The active Home link's computed background-color is {styles[0]}, expected --primary-strong {styles[2]}.");
        Assert.That(styles[1], Is.EqualTo("rgb(255, 255, 255)"),
            $"The active Home link's computed color is {styles[1]}, expected white.");
    }

    /// <summary>
    /// Task R3.0 item 1: the category count is right-aligned inside the nav item — the dead
    /// <c>.count</c> rule (<c>margin-left: auto</c>) must reach the element through the ::deep
    /// wrapper fix — and the item fills its sidebar row (the <c>.nav-item</c> flex rule), without
    /// which the right-alignment is trivially true.
    /// </summary>
    [Test]
    public async Task NavItem_CategoryCount_IsRightAlignedWithinTheItem()
    {
        await EnsureSidebarDataAsync();
        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new() { Width = 1440, Height = 900 }
        });
        await context.AddInitScriptAsync("localStorage.setItem('blog-theme', 'light')");
        var page = await context.NewPageAsync();
        await page.GotoAsync(BaseUrl);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var item = page.Locator("#sidebar-nav a.nav-item[href^='/category/']").First;
        await item.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });

        var itemBox = await item.BoundingBoxAsync();
        var countBox = await item.Locator(".count").BoundingBoxAsync();
        var rowBox = await item.Locator("xpath=..").BoundingBoxAsync();
        Assert.That(itemBox, Is.Not.Null, "The category nav item has no bounding box.");
        Assert.That(countBox, Is.Not.Null, "The category nav item's count has no bounding box.");
        Assert.That(rowBox, Is.Not.Null, "The category nav item's row has no bounding box.");

        // The item must fill its sidebar row (the .nav-item flex rule on the link). Without it an
        // unstyled item shrink-wraps its content, its right edge moves with the count, and the
        // right-alignment check below is trivially true — found by the break-it-once check (a).
        Assert.That(itemBox!.Width, Is.EqualTo(rowBox!.Width).Within(1),
            $"The nav item does not fill its sidebar row: item is {itemBox.Width}px wide in a {rowBox.Width}px row.");

        var gap = (itemBox.X + itemBox.Width) - (countBox!.X + countBox.Width);
        Assert.That(gap, Is.InRange(0, 16),
            $"The category count's right edge is {gap}px from the nav item's right edge (expected 0-16).");
    }

    /// <summary>
    /// Task R3.0 item 1: the sidebar footer items (theme switch, Log in, Collapse) share the same
    /// left x. Per ruling R3-R0-a's sibling note this pins footer geometry the NavItem wrapper
    /// fix must not disturb, so it passes before the fix too.
    /// </summary>
    [Test]
    public async Task SidebarFooter_ItemsShareTheSameLeftX()
    {
        await EnsureSidebarDataAsync();
        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new() { Width = 1440, Height = 900 }
        });
        await context.AddInitScriptAsync("localStorage.setItem('blog-theme', 'light')");
        var page = await context.NewPageAsync();
        await page.GotoAsync(BaseUrl);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var footer = page.Locator("#sidebar-nav .sidebar-footer");
        await footer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });

        var items = footer.Locator(".theme-switch, .footer-item");
        var count = await items.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(2), "The sidebar footer does not render its items.");

        var lefts = new List<double>();
        for (var i = 0; i < count; i++)
        {
            var box = await items.Nth(i).BoundingBoxAsync();
            Assert.That(box, Is.Not.Null, $"Footer item {i} has no bounding box (hidden at 1440x900?).");
            lefts.Add(box!.X);
        }

        Assert.That(lefts.Max() - lefts.Min(), Is.LessThanOrEqualTo(1),
            $"The footer items do not share the same left x: {string.Join(", ", lefts)}.");
    }

    /// <summary>
    /// The sidebar renders only categories with published posts (spec §4.2), so the layout tests
    /// need a "Programming" category with at least one published post — created through the admin
    /// UI like the other E2E suites, and only when the data is not already there.
    /// </summary>
    private async Task EnsureSidebarDataAsync()
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

            await using (var categories = await Page.APIRequest.GetAsync($"{ApiUrl}/api/categories"))
            {
                Assert.That(categories.Status, Is.EqualTo(200), "The API's category list is not reachable on :5000.");
                using var doc = JsonDocument.Parse(await categories.TextAsync());
                foreach (var category in doc.RootElement.EnumerateArray())
                {
                    if (category.GetProperty("name").GetString() == "Programming"
                        && category.GetProperty("postCount").GetInt32() > 0)
                    {
                        _dataReady = true;
                        return;
                    }
                }
            }

            await LoginAsync("admin@example.com", "password");

            await Page.GotoAsync($"{BaseUrl}/admin/tags");
            await WaitForInteractiveAsync(Page);
            await Page.GetByLabel("New category").FillAsync("Programming");
            await Page.Locator(".mud-input-control", new PageLocatorOptions { Has = Page.GetByLabel("New category") })
                .Locator("button").First
                .ClickAsync();

            await OpenNewPostEditorAsync();

            await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync($"Layout Shell Post {Guid.NewGuid():N}");
            await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).BlurAsync();
            await Page.GetByLabel("Markdown").FillAsync("Fixture post so the Programming category shows in the sidebar.");
            await Page.GetByLabel("Markdown").BlurAsync();
            // Select through the picker's search: the unfiltered list is a bounded window
            // (the first page of name-ordered categories), so "Programming" can sort outside it
            // on a database with many categories. Typing the name finds it wherever it sorts.
            var categoryPicker = Page.GetByRole(AriaRole.Combobox, new PageGetByRoleOptions { Name = "Category" });
            await categoryPicker.ClickAsync();
            await categoryPicker.FillAsync("Programming");
            await Page.GetByRole(AriaRole.Option, new PageGetByRoleOptions { Name = "Programming" }).ClickAsync();
            await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Publish now" }).ClickAsync();
            await Page.WaitForURLAsync(
                url => url.StartsWith($"{BaseUrl}/admin/posts/") && url.EndsWith("/edit"),
                new PageWaitForURLOptions { Timeout = 15000 });

            _dataReady = true;
        }
        finally
        {
            DataGate.Release();
        }
    }
}
