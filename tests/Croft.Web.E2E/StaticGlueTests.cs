namespace Croft.Web.E2E;

/// <summary>
/// Brief 09f item 2: the reading pages' JavaScript starts without a circuit — on the first load
/// and after every enhanced navigation (static-glue.js): the theme switch cycles and its label
/// follows, the sidebar collapses, the phone menu opens and closes, the scroll spy marks exactly
/// one link in the rail and one in the outline panel, the phone outline follows the scrolling,
/// expands on tap and jumps and closes on a link, code blocks are highlighted. After five
/// round trips between two long posts (enhanced navigation, the window marker survives) the same
/// checks pass and one scroll fires the spy's handler exactly once (a test-only wrapper around
/// <c>addEventListener</c>, installed from here, counts the calls by the handler's name).
/// Each test publishes its own run-keyed pair of posts that share their tags, so each post's
/// "Related" cards link to the other one.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class StaticGlueTests : StaticReadingTest
{
    private static readonly SemaphoreSlim DataGate = new(1, 1);
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];

    private static bool _dataReady;
    private static Published _postA = null!;
    private static Published _postB = null!;
    private static string _titleA = string.Empty;
    private static string _titleB = string.Empty;

    private const string CountScrollHandlers = """
        (() => {
            const wrapped = new Map();
            window.__scrollCalls = {};
            const add = window.addEventListener.bind(window);
            const remove = window.removeEventListener.bind(window);
            window.addEventListener = function (type, fn, options) {
                if (type === 'scroll' && typeof fn === 'function') {
                    let wrapper = wrapped.get(fn);
                    if (!wrapper) {
                        const name = fn.name || 'anonymous';
                        wrapper = function (...args) {
                            window.__scrollCalls[name] = (window.__scrollCalls[name] || 0) + 1;
                            return fn.apply(this, args);
                        };
                        wrapped.set(fn, wrapper);
                    }
                    return add(type, wrapper, options);
                }
                return add(type, fn, options);
            };
            window.removeEventListener = function (type, fn, options) {
                if (type === 'scroll' && wrapped.has(fn)) return remove(type, wrapped.get(fn), options);
                return remove(type, fn, options);
            };
        })();
        """;

    [TestCase(1440, 900)]
    [TestCase(390, 844)]
    public async Task ThemeSwitch_CyclesSystemLightDark_AndItsLabelFollows(int width, int height)
    {
        await EnsureFixturesAsync();
        await using var context = await NewContextAsync(width, height);
        var page = await context.NewPageAsync();
        await OpenPostAsync(page, _postA);

        await AssertThemeSwitchAsync(page, width < 960);

        // AssertThemeSwitchAsync ends on "System" with nothing stored, so after a reload the label
        // is "System" again (the stored-preference case is the next test).
        await page.ReloadAsync();
        await WaitForInteractiveAsync(page);
        await OpenMenuIfPhoneAsync(page, width < 960);
        await Expect(page.Locator(".theme-switch .label")).ToHaveTextAsync("Theme: System");
    }

    [Test]
    public async Task ThemeSwitch_StoredPreference_LabelShowsItAfterAReload()
    {
        await EnsureFixturesAsync();
        await using var context = await NewContextAsync(1440, 900);
        var page = await context.NewPageAsync();
        await OpenPostAsync(page, _postA);

        await page.Locator(".theme-switch").ClickAsync();
        await Expect(page.Locator(".theme-switch .label")).ToHaveTextAsync("Theme: Light");
        await page.ReloadAsync();
        await WaitForInteractiveAsync(page);

        await Expect(page.Locator(".theme-switch .label")).ToHaveTextAsync("Theme: Light");
        await Expect(page.Locator(".theme-switch")).ToHaveAttributeAsync("title", "Theme: Light");
        await Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "light");
    }

    [Test]
    public async Task ThemeSwitch_TheVisibleIconFollowsTheMode_OnFirstLoad_AndAfterAnEnhancedNavigation()
    {
        await EnsureFixturesAsync();
        await using var context = await NewDesktopContextAsync();
        var page = await context.NewPageAsync();
        await OpenPostAsync(page, _postA);
        await page.EvaluateAsync("() => { window.__docMarker = 'one-document'; }");

        await AssertIconCycleAsync(page);

        // The same cycle on the page an enhanced navigation lands on (the server renders the
        // button in its "System" state again; the script must put the icon right).
        await NavigateByRelatedCardAsync(page, _postB, _titleB);
        Assert.That(await page.EvaluateAsync<string?>("() => window.__docMarker"), Is.EqualTo("one-document"),
            "the navigation must be an enhanced one.");
        await AssertIconCycleAsync(page);
    }

    [Test]
    public async Task Sidebar_CollapseButton_CollapsesAndRestores()
    {
        await EnsureFixturesAsync();
        await using var context = await NewContextAsync(1440, 900);
        var page = await context.NewPageAsync();
        await OpenPostAsync(page, _postA);

        await AssertSidebarCollapseAsync(page);
    }

    [Test]
    public async Task PhoneMenu_OpensAndCloses()
    {
        await EnsureFixturesAsync();
        await using var context = await NewPhoneContextAsync();
        var page = await context.NewPageAsync();
        await OpenPostAsync(page, _postA);

        await AssertPhoneMenuAsync(page);
    }

    [TestCase(1440, 900)]
    [TestCase(390, 844)]
    public async Task ScrollSpy_MarksExactlyOneLinkInTheRailAndOneInThePanel(int width, int height)
    {
        await EnsureFixturesAsync();
        await using var context = await NewContextAsync(width, height);
        var page = await context.NewPageAsync();
        await OpenPostAsync(page, _postA);

        await AssertScrollSpyAsync(page);
    }

    [Test]
    public async Task PhoneOutline_FollowsScrolling_ExpandsOnTap_AndALinkJumpsAndCloses()
    {
        await EnsureFixturesAsync();
        await using var context = await NewPhoneContextAsync();
        var page = await context.NewPageAsync();
        await OpenPostAsync(page, _postA);

        await AssertPhoneOutlineAsync(page);
    }

    [TestCase(1440, 900)]
    [TestCase(390, 844)]
    public async Task CodeBlocks_AreHighlighted(int width, int height)
    {
        await EnsureFixturesAsync();
        await using var context = await NewContextAsync(width, height);
        var page = await context.NewPageAsync();
        await OpenPostAsync(page, _postA);

        await AssertCodeHighlightedAsync(page);
    }

    [TestCase(1440, 900)]
    [TestCase(390, 844)]
    public async Task AfterFiveEnhancedRoundTrips_EverythingStillWorks_AndAScrollFiresTheSpyOnce(int width, int height)
    {
        await EnsureFixturesAsync();
        var phone = width < 960;
        await using var context = await NewContextAsync(width, height);
        await context.AddInitScriptAsync(CountScrollHandlers);
        var page = await context.NewPageAsync();
        await OpenPostAsync(page, _postA);
        await page.EvaluateAsync("() => { window.__docMarker = 'one-document'; }");

        // Five round trips A -> B -> A through the Related cards: enhanced navigation, never a
        // full document load.
        for (var trip = 1; trip <= 5; trip++)
        {
            await NavigateByRelatedCardAsync(page, _postB, _titleB);
            await NavigateByRelatedCardAsync(page, _postA, _titleA);
        }

        Assert.That(await page.EvaluateAsync<string?>("() => window.__docMarker"), Is.EqualTo("one-document"),
            "ten navigations must stay enhanced navigations of one document (a full load would drop the marker).");

        await AssertReadingJsAsync(page, phone);

        // One scroll, one call of the scroll spy's handler: listeners never stack across the
        // navigations (each init tears the previous one down).
        // Back to the top first, and let that scroll's own event (and the frames its handlers
        // queue) land before the counters restart: a late event from this scroll would otherwise
        // be counted as the one under test.
        await page.EvaluateAsync(
            """
            async () => {
                if (window.scrollY !== 0) {
                    await new Promise(resolve => {
                        window.addEventListener('scroll', resolve, { once: true });
                        window.scrollTo(0, 0);
                    });
                }
                await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
                window.__scrollCalls = {};
            }
            """);
        var calls = await ScrollOnceAsync(page, 1200);
        Assert.That(calls.GetValueOrDefault("spyScrollHandler"), Is.EqualTo(1),
            $"one scroll must call the spy's handler once after ten enhanced navigations; counts: {System.Text.Json.JsonSerializer.Serialize(calls)}.");
        if (phone)
        {
            Assert.That(calls.GetValueOrDefault("panelScrollHandler"), Is.EqualTo(1),
                $"one scroll must call the outline panel's handler once; counts: {System.Text.Json.JsonSerializer.Serialize(calls)}.");
        }
    }

    [Test]
    public async Task ThemeAndSidebarChoices_SurviveAnEnhancedNavigation()
    {
        await EnsureFixturesAsync();
        await using var context = await NewDesktopContextAsync();
        var page = await context.NewPageAsync();
        await OpenPostAsync(page, _postA);
        await page.EvaluateAsync("() => { window.__docMarker = 'one-document'; }");

        // Dark theme and a collapsed sidebar, both set on the <html> element by script.
        await page.Locator(".theme-switch").ClickAsync();
        await page.Locator(".theme-switch").ClickAsync();
        await Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "dark");
        await page.Locator("[data-sidebar-collapse]").ClickAsync();
        await Expect(page.Locator("html")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("sidebar-collapsed"));

        await NavigateByRelatedCardAsync(page, _postB, _titleB);

        Assert.That(await page.EvaluateAsync<string?>("() => window.__docMarker"), Is.EqualTo("one-document"),
            "the navigation must be an enhanced one (one document).");
        await Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "dark");
        await Expect(page.Locator("html")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("sidebar-collapsed"));
        await Expect(page.Locator(".theme-switch .label")).ToHaveTextAsync("Theme: Dark");
    }

    [Test]
    public async Task LeavingAPost_ByEnhancedNavigation_LeavesNoScrollHandlerBehind()
    {
        await EnsureFixturesAsync();
        await using var context = await NewContextAsync(390, 844);
        await context.AddInitScriptAsync(CountScrollHandlers);
        var page = await context.NewPageAsync();
        await OpenPostAsync(page, _postA);
        await page.EvaluateAsync("() => { window.__docMarker = 'one-document'; }");

        // Home through the sidebar's Home link: an enhanced navigation to a page with no post JS.
        await page.Locator("[data-nav-toggle]:visible").First.ClickAsync();
        await page.Locator("#sidebar-nav a.nav-item[href='/']").ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Latest Posts" })).ToBeVisibleAsync();
        Assert.That(await page.EvaluateAsync<string?>("() => window.__docMarker"), Is.EqualTo("one-document"),
            "Home must open by enhanced navigation.");

        // A scroll event dispatched on the window reaches every scroll listener left on it, whatever
        // the page's height (Home need not be tall enough to scroll).
        await page.EvaluateAsync("() => { window.__scrollCalls = {}; }");
        var calls = await DispatchScrollAsync(page);
        Assert.That(calls.GetValueOrDefault("spyScrollHandler"), Is.EqualTo(0),
            "a page without a post must have no scroll spy handler left over from the post it came from.");
        Assert.That(calls.GetValueOrDefault("panelScrollHandler"), Is.EqualTo(0),
            "a page without a post must have no outline panel handler left over from the post it came from.");
    }

    // ----- the checks (shared by the first-load tests and the after-navigation test) -----

    private async Task AssertReadingJsAsync(IPage page, bool phone)
    {
        await AssertCodeHighlightedAsync(page);
        await AssertScrollSpyAsync(page);
        if (phone)
        {
            await AssertPhoneMenuAsync(page);
            await AssertPhoneOutlineAsync(page);
        }
        else
        {
            await AssertSidebarCollapseAsync(page);
        }

        await AssertThemeSwitchAsync(page, phone);
    }

    private async Task AssertCodeHighlightedAsync(IPage page)
    {
        await Expect(page.Locator(".markdown-body pre code span.hljs-keyword").First).ToBeAttachedAsync();
        Assert.That(await page.Locator(".markdown-body pre code.hljs").CountAsync(), Is.GreaterThan(0),
            "the code block must carry highlight.js's class.");
    }

    private async Task AssertThemeSwitchAsync(IPage page, bool phone)
    {
        await OpenMenuIfPhoneAsync(page, phone);
        var button = page.Locator(".theme-switch");
        var label = button.Locator(".label");

        // Back to a known start whatever an earlier check left behind: cycle until "System".
        for (var i = 0; i < 3 && await label.TextContentAsync() != "Theme: System"; i++)
        {
            await button.ClickAsync();
        }

        await Expect(label).ToHaveTextAsync("Theme: System");
        await Expect(page.Locator("html")).Not.ToHaveAttributeAsync("data-theme", "light");

        await button.ClickAsync();
        await Expect(label).ToHaveTextAsync("Theme: Light");
        await Expect(button).ToHaveAttributeAsync("title", "Theme: Light");
        await Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "light");

        await button.ClickAsync();
        await Expect(label).ToHaveTextAsync("Theme: Dark");
        await Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "dark");

        await button.ClickAsync();
        await Expect(label).ToHaveTextAsync("Theme: System");
        await Expect(page.Locator("html")).Not.ToHaveAttributeAsync("data-theme", "dark");
        await Expect(page.Locator("html")).Not.ToHaveAttributeAsync("data-theme", "light");

        await CloseMenuIfPhoneAsync(page, phone);
    }

    private async Task AssertSidebarCollapseAsync(IPage page)
    {
        var html = page.Locator("html");
        await Expect(html).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex("sidebar-collapsed"));
        await page.Locator("[data-sidebar-collapse]").ClickAsync();
        await Expect(html).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("sidebar-collapsed"));
        Assert.That(await page.EvaluateAsync<string?>("() => localStorage.getItem('blog-sidebar')"), Is.EqualTo("collapsed"),
            "the collapsed state must be remembered.");
        await page.Locator("[data-sidebar-collapse]").ClickAsync();
        await Expect(html).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex("sidebar-collapsed"));
    }

    private async Task AssertPhoneMenuAsync(IPage page)
    {
        var html = page.Locator("html");
        var toggle = page.Locator("[data-nav-toggle]:visible").First;
        await toggle.ClickAsync();
        await Expect(html).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("nav-open"));
        await Expect(toggle).ToHaveAttributeAsync("aria-expanded", "true");
        await Expect(page.Locator("#sidebar-nav a.nav-item[href='/']")).ToBeInViewportAsync();

        await page.Keyboard.PressAsync("Escape");
        await Expect(html).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex("nav-open"));
        await Expect(toggle).ToHaveAttributeAsync("aria-expanded", "false");
    }

    /// <summary>Scrolls to Section 4, then expects exactly one marked link in the rail's TOC and
    /// exactly one in the outline panel's, both naming Section 4.</summary>
    private async Task AssertScrollSpyAsync(IPage page)
    {
        await ScrollToSectionAsync(page, 3);
        var rail = page.Locator(".post-rail .toc__link[aria-current='true']");
        var panel = page.Locator(".toc-inline .toc__link[aria-current='true']");
        await Expect(rail).ToHaveCountAsync(1);
        await Expect(rail).ToHaveTextAsync("Section 4");
        await Expect(panel).ToHaveCountAsync(1);
        await Expect(panel).ToHaveTextAsync("Section 4");

        await ScrollToSectionAsync(page, 5);
        await Expect(rail).ToHaveTextAsync("Section 6");
        await Expect(rail).ToHaveCountAsync(1);
        await Expect(panel).ToHaveTextAsync("Section 6");
        await Expect(panel).ToHaveCountAsync(1);
    }

    /// <summary>The phone outline: the pinned row names the section being read and follows the
    /// scrolling, a tap on it expands the drawer, a tap on an entry jumps to that section and
    /// closes the drawer.</summary>
    private async Task AssertPhoneOutlineAsync(IPage page)
    {
        await ScrollToSectionAsync(page, 2);
        var row = page.Locator(".toc-inline__row");
        await Expect(row.Locator(".toc-inline__row-text")).ToHaveTextAsync("Section 3");
        await ScrollToSectionAsync(page, 4);
        await Expect(row.Locator(".toc-inline__row-text")).ToHaveTextAsync("Section 5");
        await Expect(row).ToHaveAttributeAsync("aria-expanded", "false");

        await row.TapAsync();
        await Expect(row).ToHaveAttributeAsync("aria-expanded", "true");

        await page.Locator(".toc-inline .toc__link", new PageLocatorOptions { HasText = "Section 7" }).TapAsync();
        await Expect(row).ToHaveAttributeAsync("aria-expanded", "false");
        await Expect(row.Locator(".toc-inline__row-text")).ToHaveTextAsync("Section 7");
        var top = await page.EvaluateAsync<double>(
            "() => document.querySelectorAll('.markdown-body h2')[6].getBoundingClientRect().top");
        Assert.That(top, Is.InRange(-1, 844 * 0.3 + 1), $"the link must have jumped to Section 7, its heading top is {top:0.#}px.");
    }

    // ----- helpers -----

    /// <summary>System → Light → Dark → System by clicking; after each click exactly one icon is
    /// rendered visible (a real box, not display:none) and it is the mode's own.</summary>
    private async Task AssertIconCycleAsync(IPage page)
    {
        // Known start: cycle until the label says System.
        var label = page.Locator(".theme-switch .label");
        for (var i = 0; i < 3 && await label.TextContentAsync() != "Theme: System"; i++)
        {
            await page.Locator(".theme-switch").ClickAsync();
        }

        await ExpectVisibleIconAsync(page, "system");
        foreach (var (mode, name) in new[] { ("light", "Light"), ("dark", "Dark"), ("system", "System") })
        {
            await page.Locator(".theme-switch").ClickAsync();
            await Expect(label).ToHaveTextAsync($"Theme: {name}");
            await ExpectVisibleIconAsync(page, mode);
        }
    }

    private static async Task ExpectVisibleIconAsync(IPage page, string mode) =>
        await page.WaitForFunctionAsync(
            """
            mode => {
                const visible = [...document.querySelectorAll('.theme-switch .theme-switch__icon')]
                    .filter(icon => icon.getClientRects().length > 0 && icon.querySelector('svg').getBoundingClientRect().width > 0);
                return visible.length === 1 && visible[0].classList.contains('theme-switch__icon--' + mode);
            }
            """, mode);

    /// <summary>Brief 13g item 8 (review I-10): a post script that cannot load must not leave the page
    /// half started. data-interactive still comes on (the end-to-end tests and the shell rely on it), the
    /// theme switch still works, and the failure is named in the console instead of vanishing as an
    /// unobserved rejection.</summary>
    [Test]
    public async Task APostScriptThatFailsToLoad_StillLeavesTheShellWorking_AndLogsTheFailure()
    {
        await EnsureFixturesAsync();
        await using var context = await NewDesktopContextAsync();
        await context.RouteAsync(new System.Text.RegularExpressions.Regex(@"post[^/]*\.js(\?.*)?$"),
            route => route.FulfillAsync(new RouteFulfillOptions { Status = 500, Body = "nope" }));
        var page = await context.NewPageAsync();
        var errorLog = EventLog.ConsoleTexts(page, "error");

        await page.GotoAsync($"{BaseUrl}/post/{_postA.Slug}");
        await WaitForInteractiveAsync(page);

        await page.Locator(".theme-switch").ClickAsync();
        await Expect(page.Locator(".theme-switch .label")).ToHaveTextAsync("Theme: Light");
        var errors = errorLog.Snapshot();
        Assert.That(errors, Has.Some.Contain("static-glue: starting the page's scripts failed"),
            $"the failure is named in the console; console errors: {string.Join(" | ", errors)}");
    }

    /// <summary>The old post's scroll handlers are torn down before the new page's scripts start, so even a
    /// start that throws leaves nothing of the previous page running against detached nodes.</summary>
    [Test]
    public async Task WhenStartingTheNextPostFails_ThePreviousPostsScrollHandlersAreStillGone()
    {
        await EnsureFixturesAsync();
        await using var context = await NewDesktopContextAsync();
        await context.AddInitScriptAsync(CountScrollHandlers);
        var page = await context.NewPageAsync();
        var errorLog = EventLog.ConsoleTexts(page, "error");
        await OpenPostAsync(page, _postA);
        // Post A is up with its spy listening. Make the next start throw (after the teardown, if there is one).
        await page.EvaluateAsync("() => { window.blogPost.highlightAll = () => { throw new Error('highlight exploded'); }; }");

        await NavigateByRelatedCardAsync(page, _postB, _titleB);

        Assert.That(errorLog.Snapshot(), Has.Some.Contain("static-glue: starting the page's scripts failed"));
        await page.EvaluateAsync(
            """
            async () => {
                await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
                window.__scrollCalls = {};
            }
            """);
        var calls = await ScrollOnceAsync(page, 600);
        Assert.That(calls.GetValueOrDefault("spyScrollHandler"), Is.Zero,
            $"post A's scroll spy must be gone after navigating to post B even though B's start failed; counts: {System.Text.Json.JsonSerializer.Serialize(calls)}.");
    }

    private async Task<IBrowserContext> NewContextAsync(int width, int height) =>
        width < 960 ? await NewPhoneContextAsync() : await NewDesktopContextAsync();

    private async Task OpenPostAsync(IPage page, Published post)
    {
        await page.GotoAsync($"{BaseUrl}/post/{post.Slug}");
        await WaitForInteractiveAsync(page);
        await page.EvaluateAsync("() => document.fonts.ready");
    }

    private async Task OpenMenuIfPhoneAsync(IPage page, bool phone)
    {
        if (phone && !await page.Locator("html.nav-open").IsVisibleAsync())
        {
            await page.Locator("[data-nav-toggle]:visible").First.ClickAsync();
            await Expect(page.Locator("html")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("nav-open"));
        }
    }

    private async Task CloseMenuIfPhoneAsync(IPage page, bool phone)
    {
        if (phone)
        {
            await page.Keyboard.PressAsync("Escape");
            await Expect(page.Locator("html")).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex("nav-open"));
        }
    }

    private static async Task ScrollToSectionAsync(IPage page, int sectionIndex) =>
        await page.EvaluateAsync("i => document.querySelectorAll('.markdown-body h2')[i].scrollIntoView()", sectionIndex);

    /// <summary>One scroll to <paramref name="y"/>; returns the wrapper's call counts after the
    /// scroll event was delivered and two frames passed (the handlers queue their work on
    /// requestAnimationFrame).</summary>
    private static async Task<Dictionary<string, int>> ScrollOnceAsync(IPage page, int y)
    {
        var json = await page.EvaluateAsync<string>(
            """
            async y => {
                // A scroll event only follows a scroll that moves the page: fail loudly (instead of
                // waiting forever) when the page cannot move to y.
                const maxY = document.documentElement.scrollHeight - window.innerHeight;
                if (Math.min(y, maxY) === window.scrollY) {
                    throw new Error(`the page cannot scroll to ${y} (scrollY ${window.scrollY}, max ${maxY})`);
                }
                await new Promise(resolve => {
                    window.addEventListener('scroll', resolve, { once: true });
                    window.scrollTo(0, y);
                });
                await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
                return JSON.stringify(window.__scrollCalls);
            }
            """, y);
        return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(json)!;
    }

    /// <summary>Dispatches one scroll event on the window; returns the wrapper's call counts after
    /// two frames.</summary>
    private static async Task<Dictionary<string, int>> DispatchScrollAsync(IPage page)
    {
        var json = await page.EvaluateAsync<string>(
            """
            async () => {
                window.dispatchEvent(new Event('scroll'));
                await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
                return JSON.stringify(window.__scrollCalls);
            }
            """);
        return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(json)!;
    }

    private async Task NavigateByRelatedCardAsync(IPage page, Published target, string targetTitle)
    {
        var card = page.Locator($"a.related-posts__link[href='/post/{target.Slug}']");
        await card.ScrollIntoViewIfNeededAsync();
        await card.ClickAsync();
        await page.WaitForURLAsync(url => url.EndsWith($"/post/{target.Slug}"));
        await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = targetTitle, Level = 1 })).ToBeVisibleAsync();
        await Expect(page.Locator("html")).ToHaveAttributeAsync("data-interactive", "on");
        await page.EvaluateAsync("() => document.fonts.ready");
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

            string[] tags = [$"glue{RunId}a", $"glue{RunId}b", $"glue{RunId}c"];
            _titleA = $"Glue Post A {RunId}";
            _titleB = $"Glue Post B {RunId}";
            _postA = await PublishAsync(_titleA, BodyWithSections(RunId, 7), tags);
            _postB = await PublishAsync(_titleB, BodyWithSections(RunId, 7), tags);
            _dataReady = true;
        }
        finally
        {
            DataGate.Release();
        }
    }
}
