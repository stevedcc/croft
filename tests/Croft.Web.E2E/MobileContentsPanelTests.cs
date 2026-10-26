using System.Text;
using System.Text.Json;

namespace Croft.Web.E2E;

/// <summary>
/// Brief 07h's mobile/tablet contents panel: at content widths where the rail is not shown
/// (below the 1000px @container rule) a post with at least two headings gets one panel with
/// three states — the in-flow block under the hero ("On this page", up to five entries, a
/// "+N more…" line, tags in a column on the right), the one-row pinned bar (the scroll-spy's
/// current section title and a chevron, 44–48px, pinned at top: 0) and the opened drawer (the
/// full list scrolling inside at most 70vh, current entry in view and highlighted, tags right).
/// Both post surfaces carry it (post page and draft review); posts without a contents panel
/// (fewer than two headings) keep the Tags panel after the text and nothing else changes.
///
/// Outcome assertions (rule 7): measured geometry and state — entry and tag boxes, the pinned
/// row's top and height against the viewport, the jumped-to heading's landing spot below the
/// row, aria-expanded and focus, and computed transition durations. Each test publishes its own
/// run-keyed post (12 h2 sections of ~150 words and 3 tags, or the shape named in the test) and
/// measures at 390x844 and 768x1024. Break-it-once evidence is in the item's commit message.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class MobileContentsPanelTests : StaticReadingTest
{
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];

    /// <summary>The brief's test widths: a phone and a tablet, both in panel mode.</summary>
    private static readonly (int Width, int Height)[] PanelViewports = [(390, 844), (768, 1024)];

    [Test]
    public async Task PostPage_BeforeScrolling_PanelUnderTheHeroShowsFiveEntriesMoreLineAndTagsRightOfTheList()
    {
        var slug = await PublishPostAsync($"R7H1 Top {RunId}", BodyWithSections(12), Tags());

        foreach (var (width, height) in PanelViewports)
        {
            await Page.SetViewportSizeAsync(width, height);
            await Page.GotoAsync($"{BaseUrl}/post/{slug}");
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Expect(Page.Locator(".markdown-body")).ToBeVisibleAsync();
            await Expect(Page.Locator(".toc-inline")).ToBeVisibleAsync();
            await Page.EvaluateAsync("() => document.fonts.ready");

            var json = await Page.EvaluateAsync<string>(
                @"() => {
                    const panel = document.querySelector('.toc-inline');
                    const hero = document.querySelector('.post-hero');
                    const list = panel.querySelector('.toc__list');
                    const entries = [...panel.querySelectorAll('.toc__link')];
                    const visible = el => {
                        const r = el.getBoundingClientRect();
                        const s = getComputedStyle(el);
                        return r.width > 0 && r.height > 0 && s.display !== 'none' && s.visibility !== 'hidden';
                    };
                    const tags = [...panel.querySelectorAll('.tag-pills a')].map(el => {
                        const r = el.getBoundingClientRect();
                        return { left: r.left, right: r.right, top: r.top };
                    });
                    const listRect = list.getBoundingClientRect();
                    const more = panel.querySelector('.toc-inline__more');
                    return JSON.stringify({
                        panelTop: panel.getBoundingClientRect().top,
                        panelBottom: panel.getBoundingClientRect().bottom,
                        heroBottom: hero.getBoundingClientRect().bottom,
                        title: panel.querySelector('.toc__title')?.textContent?.trim(),
                        entryCount: entries.length,
                        visibleEntryCount: entries.filter(visible).length,
                        moreVisible: more !== null && visible(more),
                        moreText: more ? more.textContent.trim() : null,
                        listRight: listRect.right,
                        tags
                    });
                }");

            using var doc = JsonDocument.Parse(json!);
            var r = doc.RootElement;
            Assert.That(r.GetProperty("panelTop").GetDouble(), Is.GreaterThanOrEqualTo(r.GetProperty("heroBottom").GetDouble() - 1),
                $"at {width}x{height} the panel must sit below the hero: panel top {r.GetProperty("panelTop").GetDouble():0.#}px, hero bottom {r.GetProperty("heroBottom").GetDouble():0.#}px.");
            Assert.That(r.GetProperty("title").GetString(), Is.EqualTo("On this page"),
                $"at {width}x{height} the panel must show its \"On this page\" heading.");
            Assert.That(r.GetProperty("entryCount").GetInt32(), Is.EqualTo(12),
                $"at {width}x{height} the panel must list all twelve sections' entries.");
            Assert.That(r.GetProperty("visibleEntryCount").GetInt32(), Is.EqualTo(5),
                $"at {width}x{height} the panel must show exactly five entries before scrolling.");
            Assert.That(r.GetProperty("moreVisible").GetBoolean(), Is.True,
                $"at {width}x{height} the \"+N more…\" line must be shown after the fifth entry.");
            Assert.That(r.GetProperty("moreText").GetString(), Is.EqualTo("+7 more…"),
                $"at {width}x{height} the hidden count must be 7 of 12 (U+2026 ellipsis).");

            var tags = r.GetProperty("tags").EnumerateArray().ToList();
            Assert.That(tags, Has.Count.EqualTo(3), $"at {width}x{height} the panel must carry the post's three tags.");
            var listRight = r.GetProperty("listRight").GetDouble();
            foreach (var tag in tags)
            {
                Assert.That(tag.GetProperty("left").GetDouble(), Is.GreaterThanOrEqualTo(listRight - 1),
                    $"at {width}x{height} every tag must sit in the panel's right column (tag left {tag.GetProperty("left").GetDouble():0.#}px vs list right {listRight:0.#}px).");
            }

            // Tapping "+7 more…" opens the panel (state 3) with the full list.
            await Page.Locator(".toc-inline__more").ClickAsync();
            await Expect(Page.Locator(".toc-inline .toc__link:visible")).ToHaveCountAsync(12);
        }
    }

    [Test]
    public async Task PostPage_FourSections_ShowsAllFourEntries_WithNoMoreLine()
    {
        var slug = await PublishPostAsync($"R7H1 Four {RunId}", BodyWithSections(4), Tags());

        foreach (var (width, height) in PanelViewports)
        {
            await Page.SetViewportSizeAsync(width, height);
            await Page.GotoAsync($"{BaseUrl}/post/{slug}");
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Expect(Page.Locator(".markdown-body")).ToBeVisibleAsync();
            await Page.EvaluateAsync("() => document.fonts.ready");

            await Expect(Page.Locator(".toc-inline .toc__link:visible")).ToHaveCountAsync(4);
            await Expect(Page.Locator(".toc-inline__more")).ToHaveCountAsync(0);
        }
    }

    [Test]
    public async Task PostPage_JumpToSection6_PanelPinsToOneRowAtViewportTopShowingTheCurrentTitle()
    {
        var slug = await PublishPostAsync($"R7H1 Pin {RunId}", BodyWithSections(12), Tags());

        foreach (var (width, height) in PanelViewports)
        {
            await Page.SetViewportSizeAsync(width, height);
            await Page.GotoAsync($"{BaseUrl}/post/{slug}");
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Expect(Page.Locator(".toc-inline .toc__link:visible")).ToHaveCountAsync(5);
            await Page.EvaluateAsync("() => document.fonts.ready");

            // A jump to the section (the entry-click shape): scrolled-to headings must land below
            // the pinned row, not under it.
            await Page.EvaluateAsync("() => document.getElementById('section-6').scrollIntoView()");
            await Expect(Page.Locator(".toc-inline__row-text")).ToHaveTextAsync("Section 6");
            await WaitForPanelSettledAsync("toc-inline--pinned", true);

            var json = await Page.EvaluateAsync<string>(
                @"() => {
                    const panel = document.querySelector('.toc-inline');
                    const box = panel.getBoundingClientRect();
                    const heading = document.getElementById('section-6');
                    let p = heading.nextElementSibling;
                    while (p && p.tagName !== 'P') {
                        p = p.nextElementSibling;
                    }
                    const paragraph = p ? p.getBoundingClientRect().top : null;
                    const current = panel.querySelector('.toc__link[aria-current=""true""]');
                    return JSON.stringify({
                        panelTop: box.top,
                        panelHeight: box.height,
                        rowText: panel.querySelector('.toc-inline__row-text').textContent.trim(),
                        currentText: current ? current.textContent.trim() : null,
                        paragraphTop: paragraph
                    });
                }");

            using var doc = JsonDocument.Parse(json!);
            var r = doc.RootElement;
            Assert.That(r.GetProperty("panelTop").GetDouble(), Is.EqualTo(0).Within(1),
                $"at {width}x{height} the pinned panel must sit at the viewport top (0 ±1px), was {r.GetProperty("panelTop").GetDouble():0.#}px.");
            Assert.That(r.GetProperty("panelHeight").GetDouble(), Is.InRange(44, 48),
                $"at {width}x{height} the pinned row must be 44–48px tall, was {r.GetProperty("panelHeight").GetDouble():0.#}px.");
            Assert.That(r.GetProperty("currentText").GetString(), Is.EqualTo("Section 6"),
                $"at {width}x{height} the row's title must be the scroll-spy's aria-current entry (section 6).");
            Assert.That(r.GetProperty("rowText").GetString(), Is.EqualTo("Section 6"),
                $"at {width}x{height} the row must show the current section's title, was \"{r.GetProperty("rowText").GetString()}\".");

            var paragraphTop = r.GetProperty("paragraphTop").GetDouble();
            var panelBottom = r.GetProperty("panelTop").GetDouble() + r.GetProperty("panelHeight").GetDouble();
            Assert.That(paragraphTop, Is.GreaterThanOrEqualTo(panelBottom - 1),
                $"at {width}x{height} text below the jumped-to heading must not be covered: paragraph top {paragraphTop:0.#}px, panel bottom {panelBottom:0.#}px.");
        }
    }

    [Test]
    public async Task PostPage_TappingThePinnedRow_OpensWithTheCurrentEntryVisible_AndEntryTapLandsBelowTheRow()
    {
        var slug = await PublishPostAsync($"R7H1 Open {RunId}", BodyWithSections(12), Tags());

        foreach (var (width, height) in PanelViewports)
        {
            await Page.SetViewportSizeAsync(width, height);
            await Page.GotoAsync($"{BaseUrl}/post/{slug}");
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Expect(Page.Locator(".toc-inline .toc__link:visible")).ToHaveCountAsync(5);
            await Page.EvaluateAsync("() => document.fonts.ready");

            await Page.EvaluateAsync("() => document.getElementById('section-6').scrollIntoView()");
            await Expect(Page.Locator(".toc-inline__row-text")).ToHaveTextAsync("Section 6");
            await WaitForPanelSettledAsync("toc-inline--pinned", true);

            // Tap the row: the drawer opens on the full list and the tags, on the current entry.
            var row = Page.Locator(".toc-inline__row");
            await row.ClickAsync();
            await Expect(row).ToHaveAttributeAsync("aria-expanded", "true");
            await Expect(Page.Locator(".toc-inline .toc__list")).ToBeVisibleAsync();
            await Expect(Page.Locator(".toc-inline .tag-pills")).ToBeVisibleAsync();
            await WaitForPanelSettledAsync("toc-inline--open", true);

            var json = await Page.EvaluateAsync<string>(
                @"() => {
                    const panel = document.querySelector('.toc-inline');
                    const list = panel.querySelector('.toc__list').getBoundingClientRect();
                    const link = panel.querySelector('.toc__link[aria-current=""true""]').getBoundingClientRect();
                    return JSON.stringify({
                        linkTop: link.top, linkBottom: link.bottom,
                        listTop: list.top, listBottom: list.bottom
                    });
                }");
            using (var doc = JsonDocument.Parse(json!))
            {
                var r = doc.RootElement;
                Assert.That(r.GetProperty("linkTop").GetDouble(), Is.GreaterThanOrEqualTo(r.GetProperty("listTop").GetDouble() - 1),
                    $"at {width}x{height} the current entry must be inside the list's visible box (link top {r.GetProperty("linkTop").GetDouble():0.#}px vs list top {r.GetProperty("listTop").GetDouble():0.#}px).");
                Assert.That(r.GetProperty("linkBottom").GetDouble(), Is.LessThanOrEqualTo(r.GetProperty("listBottom").GetDouble() + 1),
                    $"at {width}x{height} the current entry must be inside the list's visible box (link bottom {r.GetProperty("linkBottom").GetDouble():0.#}px vs list bottom {r.GetProperty("listBottom").GetDouble():0.#}px).");
            }

            // Tap entry 9: the panel closes and section 9 lands just below the row.
            await Page.Locator(".toc-inline", new PageLocatorOptions { HasText = "Section 6" })
                .GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = "Section 9", Exact = true })
                .ClickAsync();
            await Expect(Page.Locator(".toc-inline__row-text")).ToHaveTextAsync("Section 9");
            await Expect(row).ToHaveAttributeAsync("aria-expanded", "false");
            await WaitForPanelSettledAsync("toc-inline--open", false);

            json = await Page.EvaluateAsync<string>(
                @"() => {
                    const panel = document.querySelector('.toc-inline').getBoundingClientRect();
                    const heading = document.getElementById('section-9').getBoundingClientRect();
                    return JSON.stringify({
                        panelTop: panel.top, panelBottom: panel.bottom,
                        headingTop: heading.top
                    });
                }");
            using (var doc = JsonDocument.Parse(json!))
            {
                var r = doc.RootElement;
                var panelBottom = r.GetProperty("panelBottom").GetDouble();
                Assert.That(r.GetProperty("headingTop").GetDouble(),
                    // The landing spot is the row's height below the viewport top (scroll-margin);
                    // scroll offsets are quantised, so the heading can sit up to 1px above the
                    // panel's bottom — the suite's geometry tolerance. The 8px slack above it is
                    // the brief's.
                    Is.InRange(panelBottom - 1, panelBottom + 8),
                    $"at {width}x{height} section 9's heading must land within 8px below the panel's bottom: heading top {r.GetProperty("headingTop").GetDouble():0.#}px, panel bottom {panelBottom:0.#}px.");
            }
        }
    }

    [Test]
    public async Task PostPage_PullingTheHandleOpensAndCloses_EscapeClosesAndReturnsFocusToTheRow()
    {
        var slug = await PublishPostAsync($"R7H1 Pull {RunId}", BodyWithSections(12), Tags());

        foreach (var (width, height) in PanelViewports)
        {
            await Page.SetViewportSizeAsync(width, height);
            await Page.GotoAsync($"{BaseUrl}/post/{slug}");
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Expect(Page.Locator(".toc-inline .toc__link:visible")).ToHaveCountAsync(5);
            await Page.EvaluateAsync("() => document.fonts.ready");

            await Page.EvaluateAsync("() => document.getElementById('section-6').scrollIntoView()");
            await Expect(Page.Locator(".toc-inline__row-text")).ToHaveTextAsync("Section 6");
            await WaitForPanelSettledAsync("toc-inline--pinned", true);

            // Pull the handle down 200px with touch: the drawer opens.
            await PullHandleAsync(200);
            await Expect(Page.Locator(".toc-inline__row")).ToHaveAttributeAsync("aria-expanded", "true");
            await Expect(Page.Locator(".toc-inline .toc__link:visible")).ToHaveCountAsync(12);

            // Pull it back up: the drawer closes to the row (the list is not shown).
            await PullHandleAsync(-200);
            await Expect(Page.Locator(".toc-inline__row")).ToHaveAttributeAsync("aria-expanded", "false");
            await Expect(Page.Locator(".toc-inline .toc__list")).ToBeHiddenAsync();

            // Escape closes the drawer and returns focus to the row button.
            await Page.Locator(".toc-inline__row").ClickAsync();
            await Expect(Page.Locator(".toc-inline__row")).ToHaveAttributeAsync("aria-expanded", "true");
            await Page.Keyboard.PressAsync("Escape");
            await Expect(Page.Locator(".toc-inline__row")).ToHaveAttributeAsync("aria-expanded", "false");
            await Expect(Page.Locator(".toc-inline__row")).ToBeFocusedAsync();
        }
    }

    [Test]
    public async Task PostPage_AfterPullingTheHandle_TheNextOutsideTapStillClosesTheDrawer()
    {
        // A pull gesture is not a tap: it must never consume the click that comes after it —
        // on touch a drag beyond the tap slop produces no click at all, and the outside tap is
        // exactly how a reader closes the drawer again.
        var slug = await PublishPostAsync($"R7H1 PullTap {RunId}", BodyWithSections(12), Tags());

        foreach (var (width, height) in PanelViewports)
        {
            await Page.SetViewportSizeAsync(width, height);
            await Page.GotoAsync($"{BaseUrl}/post/{slug}");
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Expect(Page.Locator(".toc-inline .toc__link:visible")).ToHaveCountAsync(5);
            await Page.EvaluateAsync("() => document.fonts.ready");

            await Page.EvaluateAsync("() => document.getElementById('section-6').scrollIntoView()");
            await Expect(Page.Locator(".toc-inline__row-text")).ToHaveTextAsync("Section 6");
            await WaitForPanelSettledAsync("toc-inline--pinned", true);

            await PullHandleAsync(200);
            await Expect(Page.Locator(".toc-inline__row")).ToHaveAttributeAsync("aria-expanded", "true");

            // Tap outside the panel (below the drawer): the drawer closes (the tap is not
            // swallowed by the pull that just ran).
            await Page.Mouse.ClickAsync(width / 2, height - 20);
            await Expect(Page.Locator(".toc-inline__row")).ToHaveAttributeAsync("aria-expanded", "false");

            // The same must hold for a keyboard activation: pull again, then Enter on the row
            // button (a click with no pointerdown before it) must toggle the drawer.
            await PullHandleAsync(200);
            await Expect(Page.Locator(".toc-inline__row")).ToHaveAttributeAsync("aria-expanded", "true");
            await Page.Locator(".toc-inline__row").FocusAsync();
            await Page.Keyboard.PressAsync("Enter");
            await Expect(Page.Locator(".toc-inline__row")).ToHaveAttributeAsync("aria-expanded", "false");
        }
    }

    [Test]
    public async Task PostPage_NoHeadings_HasNoContentsPanel_AndTheTagsPanelStaysAfterTheText()
    {
        var slug = await PublishPostAsync($"R7H1 Plain {RunId}",
            $"Fixture prose {RunId} with no headings at all, just paragraphs of text that read on.\n\n" +
            $"Second paragraph {RunId}, still headingless, so the page has no contents panel.\n\n" +
            $"Third paragraph {RunId} closing the fixture body.\n",
            Tags());

        foreach (var (width, height) in PanelViewports)
        {
            await Page.SetViewportSizeAsync(width, height);
            await Page.GotoAsync($"{BaseUrl}/post/{slug}");
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Expect(Page.Locator(".markdown-body")).ToBeVisibleAsync();
            await Page.EvaluateAsync("() => document.fonts.ready");

            await Expect(Page.Locator(".toc-inline")).ToHaveCountAsync(0);

            var json = await Page.EvaluateAsync<string>(
                @"() => {
                    const paras = [...document.querySelectorAll('.markdown-body > p')];
                    const last = paras[paras.length - 1].getBoundingClientRect();
                    const tags = document.querySelector('.post-rail__panel--tags').getBoundingClientRect();
                    return JSON.stringify({
                        lastParagraphBottom: last.bottom,
                        tagsTop: tags.top, tagsBottom: tags.bottom
                    });
                }");
            using var doc = JsonDocument.Parse(json!);
            var r = doc.RootElement;
            Assert.That(r.GetProperty("tagsTop").GetDouble(), Is.GreaterThanOrEqualTo(r.GetProperty("lastParagraphBottom").GetDouble() - 1),
                $"at {width}x{height} the Tags panel must stay after the text on a post without a contents panel: tags top {r.GetProperty("tagsTop").GetDouble():0.#}px, text bottom {r.GetProperty("lastParagraphBottom").GetDouble():0.#}px.");
        }
    }

    [Test]
    public async Task DraftReview_PanelPinsWithTheCurrentTitle_AndTheFeedbackPanelStays()
    {
        await LoginAsync("admin@example.com", "password");
        var title = $"R7H1 Review {RunId}";
        await CreateDraftAsync(title, BodyWithSections(12), Tags());
        await LogoutAsync();
        await LoginAsync("editor@example.com", "password");

        foreach (var (width, height) in PanelViewports)
        {
            await Page.SetViewportSizeAsync(width, height);
            await OpenReviewAsync(title);
            await Page.EvaluateAsync("() => document.fonts.ready");

            await Expect(Page.Locator(".toc-inline .toc__link:visible")).ToHaveCountAsync(5);
            await Expect(Page.Locator(".feedback-panel")).ToBeVisibleAsync();

            await Page.EvaluateAsync("() => document.getElementById('section-6').scrollIntoView()");
            await Expect(Page.Locator(".toc-inline__row-text")).ToHaveTextAsync("Section 6");
            await WaitForPanelSettledAsync("toc-inline--pinned", true);

            var json = await Page.EvaluateAsync<string>(
                @"() => {
                    const box = document.querySelector('.toc-inline').getBoundingClientRect();
                    return JSON.stringify({ panelTop: box.top, panelHeight: box.height });
                }");
            using var doc = JsonDocument.Parse(json!);
            var r = doc.RootElement;
            Assert.That(r.GetProperty("panelTop").GetDouble(), Is.EqualTo(0).Within(1),
                $"at {width}x{height} the review page's panel must pin at the viewport top, was {r.GetProperty("panelTop").GetDouble():0.#}px.");
            Assert.That(r.GetProperty("panelHeight").GetDouble(), Is.InRange(44, 48),
                $"at {width}x{height} the review page's pinned row must be 44–48px tall, was {r.GetProperty("panelHeight").GetDouble():0.#}px.");

            // Entry taps on the review page land like on the post page: the heading just below
            // the row (the page's own scroll-margin), with the drawer closed.
            await Page.Locator(".toc-inline__row").ClickAsync();
            await Expect(Page.Locator(".toc-inline__row")).ToHaveAttributeAsync("aria-expanded", "true");
            await WaitForPanelSettledAsync("toc-inline--open", true);
            await Page.Locator(".toc-inline", new PageLocatorOptions { HasText = "Section 6" })
                .GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = "Section 9", Exact = true })
                .ClickAsync();
            await Expect(Page.Locator(".toc-inline__row")).ToHaveAttributeAsync("aria-expanded", "false");
            await WaitForPanelSettledAsync("toc-inline--open", false);

            json = await Page.EvaluateAsync<string>(
                @"() => {
                    const panel = document.querySelector('.toc-inline').getBoundingClientRect();
                    const heading = document.getElementById('section-9').getBoundingClientRect();
                    return JSON.stringify({ panelBottom: panel.bottom, headingTop: heading.top });
                }");
            using var doc2 = JsonDocument.Parse(json!);
            var r2 = doc2.RootElement;
            var panelBottom = r2.GetProperty("panelBottom").GetDouble();
            Assert.That(r2.GetProperty("headingTop").GetDouble(), Is.InRange(panelBottom - 1, panelBottom + 8),
                $"at {width}x{height} the review page's entry tap must land section 9's heading within 8px below the panel's bottom: heading top {r2.GetProperty("headingTop").GetDouble():0.#}px, panel bottom {panelBottom:0.#}px.");
        }
    }

    [Test]
    public async Task PostPage_PanelMotion_IsAtMost200ms_AndOffUnderReducedMotion()
    {
        var slug = await PublishPostAsync($"R7H1 Motion {RunId}", BodyWithSections(12), Tags());

        foreach (var (width, height) in PanelViewports)
        {
            await Page.SetViewportSizeAsync(width, height);
            await Page.GotoAsync($"{BaseUrl}/post/{slug}");
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Expect(Page.Locator(".toc-inline")).ToBeVisibleAsync();

            var durations = await TransitionDurationsAsync();
            Assert.That(durations, Is.Not.Empty, $"at {width}x{height} the panel must animate its state changes.");
            Assert.That(durations.Max(), Is.InRange(0.001, 0.2001),
                $"at {width}x{height} open/close and shrink must animate in more than 0 and at most 200ms, was {durations.Max() * 1000:0.#}ms.");

            await Page.EmulateMediaAsync(new PageEmulateMediaOptions { ReducedMotion = ReducedMotion.Reduce });
            var reduced = await TransitionDurationsAsync();
            Assert.That(reduced, Is.All.EqualTo(0),
                $"at {width}x{height} with prefers-reduced-motion nothing may animate, durations: {string.Join(", ", reduced)}.");
            await Page.EmulateMediaAsync(new PageEmulateMediaOptions { ReducedMotion = ReducedMotion.NoPreference });
        }
    }

    [Test]
    public async Task PostPage_WithoutJavaScript_TheBlockShowsEveryEntryInPlaceAndDoesNotStick()
    {
        // The states are a JavaScript enhancement: the prerender (and a no-JS reader) gets the
        // whole list in normal flow — nothing truncated, no dead "+N more…" button, and nothing
        // sticking over the text while it scrolls.
        var slug = await PublishPostAsync($"R7H1 NoJs {RunId}", BodyWithSections(12), Tags());

        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            JavaScriptEnabled = false,
            ViewportSize = new() { Width = 390, Height = 844 }
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/post/{slug}");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await Expect(page.Locator(".toc-inline")).ToBeVisibleAsync();
        await Expect(page.Locator(".toc-inline .toc__link:visible")).ToHaveCountAsync(12);
        await Expect(page.Locator(".toc-inline__more")).ToBeHiddenAsync();
        await Expect(page.Locator(".toc-inline")).ToHaveCSSAsync("position", "static");
    }

    [Test]
    public async Task PostPage_PanelGapsAboveAndBelow_EqualTheNoPanelTextGapBelowTheHero()
    {
        // The panel's air (brief 07i item 1): the space the hero leaves above the panel and the
        // space the panel leaves above the text must both equal the space a post without a panel
        // leaves between the hero and its first line of text. The value is measured live from the
        // control post's DOM per viewport — never hard-coded.
        var panelSlug = await PublishPostAsync($"R7I1 Gap {RunId}", BodyWithSections(12), Tags());
        var plainSlug = await PublishPostAsync($"R7I1 Plain {RunId}", PlainBody(), Tags());

        foreach (var (width, height) in PanelViewports)
        {
            await Page.SetViewportSizeAsync(width, height);

            // The control: a post with no headings has no panel, so its first line of text
            // measures the gap below the hero directly.
            await Page.GotoAsync($"{BaseUrl}/post/{plainSlug}");
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Expect(Page.Locator(".markdown-body")).ToBeVisibleAsync();
            await Expect(Page.Locator(".toc-inline")).ToHaveCountAsync(0);
            await Page.EvaluateAsync("() => document.fonts.ready");
            var control = await MeasureHeroTextGeometryAsync();
            var textGap = control.TextTop - control.HeroBottom;

            await Page.GotoAsync($"{BaseUrl}/post/{panelSlug}");
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Expect(Page.Locator(".toc-inline")).ToBeVisibleAsync();
            await Page.EvaluateAsync("() => document.fonts.ready");
            var gapped = await MeasureHeroTextGeometryAsync();

            var above = gapped.PanelTop!.Value - gapped.HeroBottom;
            var below = gapped.TextTop - gapped.PanelBottom!.Value;
            Assert.That(Math.Abs(above - textGap), Is.LessThanOrEqualTo(1),
                $"at {width}x{height} the space between the hero's bottom and the panel's top must equal the no-panel text gap ({textGap:0.#}px): panel gap {above:0.#}px.");
            Assert.That(Math.Abs(below - textGap), Is.LessThanOrEqualTo(1),
                $"at {width}x{height} the space between the panel's bottom and the text must equal the no-panel text gap ({textGap:0.#}px): text gap {below:0.#}px.");
        }
    }

    [Test]
    public async Task PostPage_OpenedDrawer_GrabBarIs36x4CentredWithItsStripBelowTheLastEntry()
    {
        // The opened drawer's handle (brief 07i item 2): a 36×4px grab bar in a ≥24px strip at the
        // drawer's bottom edge, centred in the drawer, ≥12px below the last list entry.
        var slug = await PublishPostAsync($"R7I1 Bar {RunId}", BodyWithSections(12), Tags());

        foreach (var (width, height) in PanelViewports)
        {
            await Page.SetViewportSizeAsync(width, height);
            await Page.GotoAsync($"{BaseUrl}/post/{slug}");
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Expect(Page.Locator(".toc-inline .toc__link:visible")).ToHaveCountAsync(5);
            await Page.EvaluateAsync("() => document.fonts.ready");

            await Page.EvaluateAsync("() => document.getElementById('section-6').scrollIntoView()");
            await Expect(Page.Locator(".toc-inline__row-text")).ToHaveTextAsync("Section 6");
            await WaitForPanelSettledAsync("toc-inline--pinned", true);

            await Page.Locator(".toc-inline__row").ClickAsync();
            await Expect(Page.Locator(".toc-inline__row")).ToHaveAttributeAsync("aria-expanded", "true");
            await WaitForPanelSettledAsync("toc-inline--open", true);

            await Expect(Page.Locator(".toc-inline__strip")).ToBeVisibleAsync();
            await Expect(Page.Locator(".toc-inline__grab")).ToBeVisibleAsync();

            var json = await Page.EvaluateAsync<string>(
                @"() => {
                    const panel = document.querySelector('.toc-inline').getBoundingClientRect();
                    const strip = document.querySelector('.toc-inline__strip').getBoundingClientRect();
                    const bar = document.querySelector('.toc-inline__grab').getBoundingClientRect();
                    const entries = [...document.querySelectorAll('.toc-inline .toc__link')];
                    const last = entries[entries.length - 1].getBoundingClientRect();
                    return JSON.stringify({
                        panelLeft: panel.left, panelRight: panel.right,
                        stripTop: strip.top, stripHeight: strip.height,
                        barLeft: bar.left, barWidth: bar.width, barHeight: bar.height,
                        lastEntryBottom: last.bottom
                    });
                }");
            using var doc = JsonDocument.Parse(json!);
            var r = doc.RootElement;
            var barCentre = r.GetProperty("barLeft").GetDouble() + r.GetProperty("barWidth").GetDouble() / 2;
            var drawerCentre = (r.GetProperty("panelLeft").GetDouble() + r.GetProperty("panelRight").GetDouble()) / 2;
            Assert.That(r.GetProperty("barWidth").GetDouble(), Is.EqualTo(36).Within(1),
                $"at {width}x{height} the grab bar must be 36px wide (±1px), was {r.GetProperty("barWidth").GetDouble():0.#}px.");
            Assert.That(r.GetProperty("barHeight").GetDouble(), Is.EqualTo(4).Within(1),
                $"at {width}x{height} the grab bar must be 4px tall (±1px), was {r.GetProperty("barHeight").GetDouble():0.#}px.");
            Assert.That(Math.Abs(barCentre - drawerCentre), Is.LessThanOrEqualTo(1),
                $"at {width}x{height} the grab bar must be centred in the drawer (±1px): bar centre {barCentre:0.#}px, drawer centre {drawerCentre:0.#}px.");
            Assert.That(r.GetProperty("stripHeight").GetDouble(), Is.GreaterThanOrEqualTo(24),
                $"at {width}x{height} the bar's strip must be at least 24px tall, was {r.GetProperty("stripHeight").GetDouble():0.#}px.");
            Assert.That(r.GetProperty("stripTop").GetDouble() - r.GetProperty("lastEntryBottom").GetDouble(), Is.GreaterThanOrEqualTo(12),
                $"at {width}x{height} the strip must sit at least 12px below the last entry: strip top {r.GetProperty("stripTop").GetDouble():0.#}px, last entry bottom {r.GetProperty("lastEntryBottom").GetDouble():0.#}px.");
        }
    }

    [Test]
    public async Task PostPage_PullingTheStripUpClosesTheDrawer_FocusingTheRow_AndClosedStatesShowNoBar()
    {
        // The strip is the drawer's pull-up target (brief 07i item 2): pulling it up closes and
        // returns focus to the row button. Closed — in the block or the pinned row — no bar shows.
        var slug = await PublishPostAsync($"R7I1 Strip {RunId}", BodyWithSections(12), Tags());

        foreach (var (width, height) in PanelViewports)
        {
            await Page.SetViewportSizeAsync(width, height);
            await Page.GotoAsync($"{BaseUrl}/post/{slug}");
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Expect(Page.Locator(".toc-inline .toc__link:visible")).ToHaveCountAsync(5);
            await Page.EvaluateAsync("() => document.fonts.ready");

            // The block (closed) shows no bar.
            await Expect(Page.Locator(".toc-inline__strip")).ToBeHiddenAsync();
            await Expect(Page.Locator(".toc-inline__grab")).ToBeHiddenAsync();

            await Page.EvaluateAsync("() => document.getElementById('section-6').scrollIntoView()");
            await Expect(Page.Locator(".toc-inline__row-text")).ToHaveTextAsync("Section 6");
            await WaitForPanelSettledAsync("toc-inline--pinned", true);

            // The pinned row (closed) shows no bar either.
            await Expect(Page.Locator(".toc-inline__strip")).ToBeHiddenAsync();
            await Expect(Page.Locator(".toc-inline__grab")).ToBeHiddenAsync();

            await Page.Locator(".toc-inline__row").ClickAsync();
            await Expect(Page.Locator(".toc-inline__row")).ToHaveAttributeAsync("aria-expanded", "true");
            await WaitForPanelSettledAsync("toc-inline--open", true);
            await Expect(Page.Locator(".toc-inline__strip")).ToBeVisibleAsync();

            await PullSurfaceAsync(".toc-inline__strip", -150);
            await Expect(Page.Locator(".toc-inline__row")).ToHaveAttributeAsync("aria-expanded", "false");
            await WaitForPanelSettledAsync("toc-inline--open", false);
            await Expect(Page.Locator(".toc-inline__row")).ToBeFocusedAsync();
            await Expect(Page.Locator(".toc-inline__strip")).ToBeHiddenAsync();
        }
    }

    [Test]
    public async Task PostPage_OpenedDrawer_ShowsTheScrimBelowTheDrawer_AtTheModeAlphaInLightAndDark()
    {
        // The scrim (brief 07i item 3): while the drawer is open the page below it dims behind
        // black at the theme's scrim alpha — 32% light, 48% dark — asserted from the computed
        // background colour (CSS variables resolved) at a point in the text below the drawer.
        var slug = await PublishPostAsync($"R7I1 Scrim {RunId}", BodyWithSections(12), Tags());

        foreach (var (mode, alpha) in new[] { ("light", 0.32), ("dark", 0.48) })
        {
            foreach (var (width, height) in PanelViewports)
            {
                await Page.SetViewportSizeAsync(width, height);
                await SetThemePreferenceAsync(mode);
                await Page.GotoAsync($"{BaseUrl}/post/{slug}");
                await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                await Expect(Page.Locator(".toc-inline")).ToBeVisibleAsync();
                await Page.EvaluateAsync("() => document.fonts.ready");

                await Page.EvaluateAsync("() => document.getElementById('section-6').scrollIntoView()");
                await Expect(Page.Locator(".toc-inline__row-text")).ToHaveTextAsync("Section 6");
                await WaitForPanelSettledAsync("toc-inline--pinned", true);
                await Page.Locator(".toc-inline__row").ClickAsync();
                await Expect(Page.Locator(".toc-inline__row")).ToHaveAttributeAsync("aria-expanded", "true");
                await WaitForPanelSettledAsync("toc-inline--open", true);

                var point = await HitPointBelowDrawerAsync();
                Assert.That(point.IsScrim, Is.True,
                    $"at {mode} {width}x{height} the element in the text area below the drawer must be the scrim, was {point.HitTag}.");
                Assert.That(point.BackgroundAlpha, Is.EqualTo(alpha).Within(0.01),
                    $"at {mode} {width}x{height} the scrim's computed background alpha must be {alpha:0.00} (±0.01), was {point.BackgroundAlpha:0.00} ({point.Background}).");
            }
        }
    }

    [Test]
    public async Task PostPage_TappingTheScrimClosesTheDrawer_AndClosedKeepsItOutOfTheHitTest()
    {
        // Tapping the scrim closes the drawer (as tapping outside always did) and a closed panel
        // leaves no scrim in the hit test.
        var slug = await PublishPostAsync($"R7I1 Tap {RunId}", BodyWithSections(12), Tags());

        foreach (var (width, height) in PanelViewports)
        {
            await Page.SetViewportSizeAsync(width, height);
            await Page.GotoAsync($"{BaseUrl}/post/{slug}");
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Expect(Page.Locator(".toc-inline .toc__link:visible")).ToHaveCountAsync(5);
            await Page.EvaluateAsync("() => document.fonts.ready");

            await Page.EvaluateAsync("() => document.getElementById('section-6').scrollIntoView()");
            await Expect(Page.Locator(".toc-inline__row-text")).ToHaveTextAsync("Section 6");
            await WaitForPanelSettledAsync("toc-inline--pinned", true);
            await Page.Locator(".toc-inline__row").ClickAsync();
            await Expect(Page.Locator(".toc-inline__row")).ToHaveAttributeAsync("aria-expanded", "true");
            await WaitForPanelSettledAsync("toc-inline--open", true);

            var point = await HitPointBelowDrawerAsync();
            Assert.That(point.IsScrim, Is.True,
                $"at {width}x{height} the tap target below the drawer must be the scrim, was {point.HitTag}.");

            await Page.Mouse.ClickAsync(point.X, point.Y);
            await Expect(Page.Locator(".toc-inline__row")).ToHaveAttributeAsync("aria-expanded", "false");
            await WaitForPanelSettledAsync("toc-inline--open", false);
            await Page.WaitForFunctionAsync(
                "() => { const s = document.querySelector('.toc-inline__scrim'); return s !== null && getComputedStyle(s).visibility === 'hidden'; }");

            var closed = await Page.EvaluateAsync<bool>(
                @"([x, y]) => {
                    const el = document.elementFromPoint(x, y);
                    return el !== null && el.classList.contains('toc-inline__scrim');
                }", new object[] { point.X, point.Y });
            Assert.That(closed, Is.False,
                $"at {width}x{height} a closed panel must leave no scrim in the hit test.");
        }
    }

    [Test]
    public async Task PostPage_ScrimFadesWithTheDrawer_InAtMost200ms_AndNotUnderReducedMotion()
    {
        // The scrim fades with the open/close animation — at most 200ms — and not at all under
        // prefers-reduced-motion (the same outcome measure as the panel's own motion test).
        var slug = await PublishPostAsync($"R7I1 Fade {RunId}", BodyWithSections(12), Tags());

        foreach (var (width, height) in PanelViewports)
        {
            await Page.SetViewportSizeAsync(width, height);
            await Page.GotoAsync($"{BaseUrl}/post/{slug}");
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Expect(Page.Locator(".toc-inline__scrim")).ToHaveCountAsync(1);

            var durations = await ScrimTransitionDurationsAsync();
            Assert.That(durations, Is.Not.Empty, $"at {width}x{height} the scrim must animate its fade.");
            Assert.That(durations.Max(), Is.InRange(0.001, 0.2001),
                $"at {width}x{height} the scrim must fade in more than 0 and at most 200ms, was {durations.Max() * 1000:0.#}ms.");

            await Page.EmulateMediaAsync(new PageEmulateMediaOptions { ReducedMotion = ReducedMotion.Reduce });
            var reduced = await ScrimTransitionDurationsAsync();
            Assert.That(reduced, Is.All.EqualTo(0),
                $"at {width}x{height} with prefers-reduced-motion the scrim may not animate, durations: {string.Join(", ", reduced)}.");
            await Page.EmulateMediaAsync(new PageEmulateMediaOptions { ReducedMotion = ReducedMotion.NoPreference });
        }
    }

    /// <summary>The brief's item-2 widths: a phone and a small phone, both in panel mode.</summary>
    private static readonly (int Width, int Height)[] ScrimViewports = [(390, 844), (360, 740)];

    [Test]
    public async Task PostPage_OpenedDrawer_ScrimTopIsTheDrawerBottom_FromTheBlockAndThePinnedRow_InLight()
    {
        // The scrim's top edge is the drawer's bottom edge after the drawer opens (brief 07j
        // item 2): measured with no scrolling in between — a scroll re-syncs the scrim and would
        // mask the bug this test is here to catch. Both open paths (the in-flow block's
        // "+N more…" and the pinned row after scrolling to mid-post) at both widths, light.
        var slug = await PublishPostAsync($"R7J2 Scrim {RunId}", BodyWithSections(12), Tags());
        await AssertScrimTopIsDrawerBottomFromBothOpenWaysAsync(slug);
    }

    [Test]
    public async Task PostPage_OpenedDrawer_ScrimTopIsTheDrawerBottom_UnderReducedMotion_InLight()
    {
        // Under prefers-reduced-motion the drawer's height does not transition: the immediate
        // read is already right and must stay right (brief 07j item 2).
        var slug = await PublishPostAsync($"R7J2 Reduced {RunId}", BodyWithSections(12), Tags());
        await Page.EmulateMediaAsync(new PageEmulateMediaOptions { ReducedMotion = ReducedMotion.Reduce });
        await AssertScrimTopIsDrawerBottomFromBothOpenWaysAsync(slug);
        await Page.EmulateMediaAsync(new PageEmulateMediaOptions { ReducedMotion = ReducedMotion.NoPreference });
    }

    /// <summary>
    /// Stores the theme preference the next page load reads. localStorage belongs to the site's origin and
    /// a fresh page is about:blank, which has none (these tests used to arrive with the page already on the
    /// site, left there by publishing through the editor), so the page first goes to the site.
    /// </summary>
    private async Task SetThemePreferenceAsync(string mode)
    {
        if (!Page.Url.StartsWith(BaseUrl, StringComparison.Ordinal))
        {
            await Page.GotoAsync($"{BaseUrl}/");
        }

        await Page.EvaluateAsync<string>("mode => { localStorage.setItem('blog-theme', mode); return mode; }", mode);
    }

    /// <summary>Opens the drawer both ways at the brief's widths — from the in-flow block, then
    /// from the pinned row after scrolling to mid-post — and measures the scrim against the
    /// drawer after each open.</summary>
    private async Task AssertScrimTopIsDrawerBottomFromBothOpenWaysAsync(string slug)
    {
        foreach (var (width, height) in ScrimViewports)
        {
            await Page.SetViewportSizeAsync(width, height);
            await SetThemePreferenceAsync("light");
            await Page.GotoAsync($"{BaseUrl}/post/{slug}");
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Expect(Page.Locator(".toc-inline .toc__link:visible")).ToHaveCountAsync(5);
            await Page.EvaluateAsync("() => document.fonts.ready");

            // From the in-flow block: "+7 more…" opens the drawer. The tap is dispatched in-page
            // (never a Playwright click — its auto-scroll would move the page before the read).
            await Page.EvaluateAsync("() => document.querySelector('.toc-inline__more').click()");
            await Expect(Page.Locator(".toc-inline__row")).ToHaveAttributeAsync("aria-expanded", "true");
            await AssertScrimTopIsDrawerBottomAsync(width, height, "opened from the in-flow block");

            // Close, scroll to mid-post, open from the pinned row. The scroll is complete before
            // the drawer opens — the measurement window after the tap must be scroll-free.
            await Page.Keyboard.PressAsync("Escape");
            await Expect(Page.Locator(".toc-inline__row")).ToHaveAttributeAsync("aria-expanded", "false");
            await WaitForPanelSettledAsync("toc-inline--open", false);
            await Page.EvaluateAsync("() => document.getElementById('section-6').scrollIntoView()");
            await Expect(Page.Locator(".toc-inline__row-text")).ToHaveTextAsync("Section 6");
            await WaitForPanelSettledAsync("toc-inline--pinned", true);
            await Page.Locator(".toc-inline__row").ClickAsync();
            await Expect(Page.Locator(".toc-inline__row")).ToHaveAttributeAsync("aria-expanded", "true");
            await AssertScrimTopIsDrawerBottomAsync(width, height, "opened from the pinned row");
        }
    }

    /// <summary>The brief's item-2 observation (rule 7 outcome): with the drawer open and no
    /// scrolling, the scrim's top edge equals the drawer's bottom edge (±1 px). The brief's
    /// "wait 300 ms" observation window (the height transition is 200 ms) is held to the
    /// concrete "no running animation" condition before the read, so a lagging transition can
    /// never flake the geometry (AGENTS.md rule 6: the wait hides nothing — the assertion is
    /// the exact geometry).</summary>
    private async Task AssertScrimTopIsDrawerBottomAsync(int width, int height, string how)
    {
        await WaitForPanelSettledAsync("toc-inline--open", true);
        var json = await Page.EvaluateAsync<string>(
            @"() => {
                const panel = document.querySelector('.toc-inline').getBoundingClientRect();
                const scrim = document.querySelector('.toc-inline__scrim').getBoundingClientRect();
                return JSON.stringify({ scrimTop: scrim.top, drawerBottom: panel.bottom });
            }");
        using var doc = JsonDocument.Parse(json!);
        var r = doc.RootElement;
        Assert.That(r.GetProperty("scrimTop").GetDouble(), Is.EqualTo(r.GetProperty("drawerBottom").GetDouble()).Within(1),
            $"at {width}x{height} ({how}) the scrim's top must be the drawer's bottom (±1px): scrim top {r.GetProperty("scrimTop").GetDouble():0.#}px, drawer bottom {r.GetProperty("drawerBottom").GetDouble():0.#}px.");
    }

    /// <summary>Waits for the panel's state change to be in place and its ≤200ms transitions done
    /// before geometry is read — a concrete condition (the state class and no running animation),
    /// never a sleep.</summary>
    private async Task WaitForPanelSettledAsync(string stateClass, bool present)
    {
        await Page.WaitForFunctionAsync(
            @"args => {
                const el = document.querySelector('.toc-inline');
                return el !== null
                    && el.classList.contains(args.cls) === args.present
                    && el.getAnimations({ subtree: true }).length === 0;
            }",
            new { cls = stateClass, present });
    }

    /// <summary>Hero, panel and first-text geometry at the current scroll position (brief 07i
    /// item 1): the hero's bottom edge, the body's first line of text and — when this post
    /// carries a panel — the panel's top and bottom edges, read in one pass so gap comparisons
    /// measure one layout, never two.</summary>
    private sealed record HeroTextGeometry(double HeroBottom, double TextTop, double? PanelTop, double? PanelBottom);

    private async Task<HeroTextGeometry> MeasureHeroTextGeometryAsync()
    {
        var json = await Page.EvaluateAsync<string>(
            @"() => {
                const hero = document.querySelector('.post-hero').getBoundingClientRect();
                const text = document.querySelector('.markdown-body > p').getBoundingClientRect();
                const panel = document.querySelector('.toc-inline');
                const box = panel === null ? null : panel.getBoundingClientRect();
                return JSON.stringify({
                    heroBottom: hero.bottom,
                    textTop: text.top,
                    panelTop: box === null ? null : box.top,
                    panelBottom: box === null ? null : box.bottom
                });
            }");
        using var doc = JsonDocument.Parse(json!);
        var r = doc.RootElement;
        return new HeroTextGeometry(
            r.GetProperty("heroBottom").GetDouble(),
            r.GetProperty("textTop").GetDouble(),
            r.GetProperty("panelTop").ValueKind == JsonValueKind.Number ? r.GetProperty("panelTop").GetDouble() : null,
            r.GetProperty("panelBottom").ValueKind == JsonValueKind.Number ? r.GetProperty("panelBottom").GetDouble() : null);
    }

    /// <summary>The outcome at a point in the text area below the opened drawer (brief 07i
    /// item 3): the element the hit test finds there and its computed background alpha — the
    /// scrim must be the element and its alpha the theme's scrim value. The alpha is parsed in
    /// the browser from whichever colour syntax the computed style carries (rgba(...) or
    /// color(srgb ... / a)). The point itself is integer (Math.round): the coordinates cross
    /// back into the page for the tap and the closed-state hit test.</summary>
    private sealed record ScrimHitPoint(int X, int Y, bool IsScrim, string HitTag, double BackgroundAlpha, string Background);

    private async Task<ScrimHitPoint> HitPointBelowDrawerAsync()
    {
        var json = await Page.EvaluateAsync<string>(
            @"() => {
                const panel = document.querySelector('.toc-inline');
                const box = panel.getBoundingClientRect();
                const x = Math.round(box.left + box.width / 2);
                const y = Math.round(Math.min(box.bottom + 60, window.innerHeight - 24));
                const el = document.elementFromPoint(x, y);
                const bg = el === null ? '' : getComputedStyle(el).backgroundColor;
                let alpha = 1;
                const modern = bg.match(/color\(srgb[^)]*\)/);
                const legacy = bg.match(/rgba?\(([^)]*)\)/);
                if (modern !== null) {
                    const a = modern[0].match(/\/\s*([\d.]+)/);
                    alpha = a === null ? 1 : parseFloat(a[1]);
                } else if (legacy !== null) {
                    const parts = legacy[1].split(/[\s,\/]+/).filter(v => v.length > 0);
                    if (parts.length >= 4) {
                        alpha = parseFloat(parts[3]);
                    }
                }
                const tag = el === null ? 'nothing'
                    : el.tagName.toLowerCase()
                        + (typeof el.className === 'string' && el.className ? '.' + el.className.trim().replace(/\s+/g, '.') : '');
                return JSON.stringify({
                    x, y,
                    isScrim: el !== null && el.classList.contains('toc-inline__scrim'),
                    hitTag: tag,
                    backgroundAlpha: alpha,
                    background: bg
                });
            }");
        using var doc = JsonDocument.Parse(json!);
        var r = doc.RootElement;
        return new ScrimHitPoint(
            (int)Math.Round(r.GetProperty("x").GetDouble()),
            (int)Math.Round(r.GetProperty("y").GetDouble()),
            r.GetProperty("isScrim").GetBoolean(),
            r.GetProperty("hitTag").GetString()!,
            r.GetProperty("backgroundAlpha").GetDouble(),
            r.GetProperty("background").GetString()!);
    }

    /// <summary>Drags the panel's handle by dy pixels in one pointer gesture of touch type
    /// (the brief's pointer-events path): down, move, up — a pull down opens, a pull up closes.</summary>
    private async Task PullHandleAsync(int dy)
    {
        await Page.EvaluateAsync<bool>(
            @"dy => {
                const handle = document.querySelector('.toc-inline__handle');
                const r = handle.getBoundingClientRect();
                const x = r.x + r.width / 2;
                const y = r.y + r.height / 2;
                const init = { pointerType: 'touch', pointerId: 1, isPrimary: true, bubbles: true, cancelable: true };
                handle.dispatchEvent(new PointerEvent('pointerdown', { ...init, clientX: x, clientY: y }));
                handle.dispatchEvent(new PointerEvent('pointermove', { ...init, clientX: x, clientY: y + dy }));
                handle.dispatchEvent(new PointerEvent('pointerup', { ...init, clientX: x, clientY: y + dy }));
                return true;
            }", dy);
    }

    /// <summary>Every transition duration on the panel (seconds), from its computed style.</summary>
    private async Task<IReadOnlyList<double>> TransitionDurationsAsync()
    {
        var json = await Page.EvaluateAsync<string>(
            @"() => {
                const s = getComputedStyle(document.querySelector('.toc-inline'));
                return JSON.stringify(s.transitionDuration.split(',').map(v => parseFloat(v) || 0));
            }");
        return JsonSerializer.Deserialize<double[]>(json!)!;
    }

    /// <summary>Every transition duration on the scrim (seconds), from its computed style — the
    /// same measure as TransitionDurationsAsync, on the dim behind the opened drawer.</summary>
    private async Task<IReadOnlyList<double>> ScrimTransitionDurationsAsync()
    {
        var json = await Page.EvaluateAsync<string>(
            @"() => {
                const s = getComputedStyle(document.querySelector('.toc-inline__scrim'));
                return JSON.stringify(s.transitionDuration.split(',').map(v => parseFloat(v) || 0));
            }");
        return JsonSerializer.Deserialize<double[]>(json!)!;
    }

    /// <summary>Drags whichever pull surface the panel's states expose — selector names it (the
    /// row's handle at the top, the grab strip at the drawer's bottom) — by dy pixels in one
    /// pointer gesture of touch type, the same gesture PullHandleAsync drives on the handle.</summary>
    private async Task PullSurfaceAsync(string selector, int dy)
    {
        await Page.EvaluateAsync<bool>(
            @"([sel, dy]) => {
                const handle = document.querySelector(sel);
                const r = handle.getBoundingClientRect();
                const x = r.x + r.width / 2;
                const y = r.y + r.height / 2;
                const init = { pointerType: 'touch', pointerId: 1, isPrimary: true, bubbles: true, cancelable: true };
                handle.dispatchEvent(new PointerEvent('pointerdown', { ...init, clientX: x, clientY: y }));
                handle.dispatchEvent(new PointerEvent('pointermove', { ...init, clientX: x, clientY: y + dy }));
                handle.dispatchEvent(new PointerEvent('pointerup', { ...init, clientX: x, clientY: y + dy }));
                return true;
            }", new object[] { selector, dy });
    }

    /// <summary>Run-keyed tags so the panel has its tags column.</summary>
    private static string[] Tags() => [$"r7h1a{RunId}", $"r7h1b{RunId}", $"r7h1c{RunId}"];

    /// <summary>An intro plus N h2 sections of 6 × 26 words (the brief's ~150 words each) — a body
    /// tall enough to pin the panel and jump between sections.</summary>
    private static string BodyWithSections(int sections, int sentenceRepeats = 6)
    {
        var body = new StringBuilder();
        body.Append($"Fixture intro {RunId} with enough words to start the page comfortably.\n\n");
        for (var n = 1; n <= sections; n++)
        {
            body.Append($"## Section {n}\n\n");
            for (var repeat = 0; repeat < sentenceRepeats; repeat++)
            {
                body.Append(Sentence).Append(". ");
            }

            body.Append("\n\n");
        }

        return body.ToString();
    }

    /// <summary>A body with no headings at all: the post renders no contents panel, so its
    /// first line of text is the control the panel's own hero gaps are measured against
    /// (brief 07i item 1).</summary>
    private static string PlainBody() =>
        $"Gap control {RunId}: a post with no headings at all, only paragraphs of running text.\n\n"
        + $"Second paragraph {RunId}, still headingless, keeping the control body plain.\n\n"
        + $"Third paragraph {RunId} closes the control post's body.\n";

    private async Task LogoutAsync()
    {
        await SignOutAsync();
    }

    /// <summary>
    /// Publishes a post through the API as the mock admin (one request; the shape of the post is
    /// what these tests measure, so the editor and its IdP login add nothing but time).
    /// </summary>
    private async Task<string> PublishPostAsync(string title, string body, params string[] tags)
    {
        var published = await PublishAsync(title, body, tags: tags);
        return published.Slug;
    }

    /// <summary>Saves a post as a draft through the admin editor (EditorTests' fixture rule).</summary>
    private async Task CreateDraftAsync(string title, string body, params string[] tags)
    {
        await OpenNewPostEditorAsync();
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync(title);
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).BlurAsync();
        await Page.GetByLabel("Markdown").FillAsync(body);
        await Page.GetByLabel("Markdown").BlurAsync();
        await AddTagsAsync(tags);

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save draft" }).ClickAsync();
        await Page.WaitForURLAsync(
            url => url.StartsWith($"{BaseUrl}/admin/posts/") && url.EndsWith("/edit"),
            new PageWaitForURLOptions { Timeout = 30000 });
    }

    /// <summary>Opens the draft's review page through the Editor list (its Review link carries
    /// the slug the public API cannot reveal).</summary>
    private async Task OpenReviewAsync(string title)
    {
        await Page.GotoAsync($"{BaseUrl}/editor");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        var row = Page.Locator(".posts-grid__row", new PageLocatorOptions { HasText = title });
        await Expect(row).ToBeVisibleAsync();
        await row.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = "Review" }).ClickAsync();
        await Page.WaitForURLAsync(
            url => url.StartsWith($"{BaseUrl}/editor/posts/"),
            new PageWaitForURLOptions { Timeout = 15000 });
        await Expect(Page.Locator(".post-hero__ribbon")).ToHaveTextAsync("Draft");
    }
}
