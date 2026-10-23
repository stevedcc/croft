using System.Text;
using System.Text.Json;

namespace Croft.Web.E2E;

/// <summary>
/// The post rail's top group — Tags, then "On this page" — must stay in view while the post
/// scrolls away: at content widths where the rail shows (the ≥1000px @container rule) the group
/// sticks 24px below the viewport top, is capped at the viewport height minus 48px (the TOC
/// scrolls inside it, Tags stay at the top) and the scroll-spy's current entry is kept inside the
/// TOC's own visible box. Below 1000px of content width no rail element becomes sticky and the
/// "On this page" panel stays above the text (at load: at rest — no drawer, no pinned row). Both post surfaces are covered: the public post
/// page and the draft review page, whose Feedback panel renders above the sticky group so the
/// group can never slide over it.
///
/// Outcomes are measured geometry and computed styles (rule 7): sticky offsets after a real
/// scroll, the capped group's bottom edge, scrollHeight vs clientHeight of the TOC's own box, the
/// active entry's rectangle against that box, and computed position on every rail element at
/// phone width. Break-it-once (recorded in the commit): removing position: sticky fails the 24px
/// test; removing the in-TOC scroll fails the 40-section test.
///
/// Fixtures (the suite's rule): each test publishes its own run-keyed post through the admin UI,
/// with N h2 sections of the brief's ~150 words each and 3 tags (so the sticky group has both
/// halves).
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class PostRailStickyTests : StaticReadingTest
{
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];

    [Test]
    public async Task PostPage_LongPost_AfterScrollToLastSection_TagsStick24pxBelowTheViewportTop_WithTocFullyVisible()
    {
        // The brief's fixture: 12 h2 sections of ~150 words each and 3 tags, measured at both
        // wide viewports (the post page's rail shows at ≥1000px of content width).
        var slug = await PublishPostAsync($"R7D2 Sticky {RunId}", BodyWithSections(12), Tags());

        foreach (var (width, height) in new[] { (1440, 900), (2560, 1440) })
        {
            await Page.SetViewportSizeAsync(width, height);
            await Page.GotoAsync($"{BaseUrl}/post/{slug}");
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Expect(Page.Locator(".post-rail .toc")).ToBeVisibleAsync();
            await Page.EvaluateAsync("() => document.fonts.ready");

            // Scroll to the last section (its heading to the top of the viewport, clamped by the
            // end of the document). The scroll is synchronous; the sticky group must be parked at
            // its 24px offset by the time the boxes are read.
            await Page.EvaluateAsync(
                @"() => {
                    const headings = document.querySelectorAll('.markdown-body h2');
                    const last = headings[headings.length - 1];
                    window.scrollTo(0, last.getBoundingClientRect().top + window.scrollY);
                }");

            var json = await Page.EvaluateAsync<string>(
                @"() => {
                    const panels = [...document.querySelectorAll('.post-rail section.post-rail__panel')];
                    const tags = panels.find(s => s.querySelector('.tag-pills')).getBoundingClientRect();
                    const toc = document.querySelector('.post-rail .toc').getBoundingClientRect();
                    return JSON.stringify({
                        tagsTop: tags.top, tagsBottom: tags.bottom,
                        tocTop: toc.top, tocBottom: toc.bottom,
                        viewportHeight: window.innerHeight
                    });
                }");

            using var doc = JsonDocument.Parse(json!);
            var r = doc.RootElement;
            var tagsTop = r.GetProperty("tagsTop").GetDouble();
            var tagsBottom = r.GetProperty("tagsBottom").GetDouble();
            var tocTop = r.GetProperty("tocTop").GetDouble();
            var tocBottom = r.GetProperty("tocBottom").GetDouble();
            var viewportHeight = r.GetProperty("viewportHeight").GetDouble();

            Assert.That(tagsTop, Is.EqualTo(24).Within(1),
                $"at {width}x{height} the Tags panel's top must stick 24px below the viewport top after scrolling to the last section, was {tagsTop:0.#}px.");
            Assert.That(tocTop, Is.GreaterThanOrEqualTo(-1),
                $"at {width}x{height} the TOC is not fully inside the viewport: top {tocTop:0.#}px.");
            Assert.That(tocBottom, Is.LessThanOrEqualTo(viewportHeight + 1),
                $"at {width}x{height} the TOC is not fully inside the viewport: bottom {tocBottom:0.#}px of {viewportHeight:0.#}px.");
            Assert.That(tagsBottom, Is.LessThanOrEqualTo(tocTop + 1),
                $"at {width}x{height} the Tags panel must sit above the TOC: tags bottom {tagsBottom:0.#}px, TOC top {tocTop:0.#}px.");
        }
    }

    [Test]
    public async Task PostPage_ManySections_GroupIsCapped_TocScrollsAndKeepsTheCurrentEntryVisible()
    {
        // 40 sections make the group far taller than the viewport: the group must cap at
        // viewport height − 48px, the TOC's own box must scroll inside it, and the scroll-spy's
        // active entry must be scrolled into that box without moving the page. The brief fixes
        // the section COUNT here; the bodies are the same ~150 words as the 12-section posts.
        var slug = await PublishPostAsync($"R7D2 Cap {RunId}", BodyWithSections(40), Tags());

        await Page.SetViewportSizeAsync(1440, 900);
        await Page.GotoAsync($"{BaseUrl}/post/{slug}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.Locator(".post-rail .toc")).ToBeVisibleAsync();
        await Page.EvaluateAsync("() => document.fonts.ready");

        var headings = await Page.EvaluateAsync<int>("() => document.querySelectorAll('.markdown-body h2').length");
        Assert.That(headings, Is.EqualTo(40), "the fixture must render 40 h2 sections.");

        // Put section 35 100px below the viewport top and remember the exact scroll position the
        // test set.
        var scrollY = await Page.EvaluateAsync<double>(
            @"() => {
                const heading = document.querySelectorAll('.markdown-body h2')[34];
                window.scrollTo(0, heading.getBoundingClientRect().top + window.scrollY - 100);
                return window.scrollY;
            }");

        // Wait for the scroll-spy to settle on the section reached: the last heading at or above
        // the top third of the viewport — section 35 (or the one just after it), deep down the
        // 40-entry TOC. The spy's marking is not the feature under test; this only waits until
        // it has reacted to the scroll.
        await Page.WaitForFunctionAsync(
            @"() => {
                const links = [...document.querySelectorAll('.post-rail .toc__link')];
                return links.findIndex(l => l.getAttribute('aria-current') === 'true') >= 30;
            }");

        var json = await Page.EvaluateAsync<string>(
            @"() => {
                const toc = document.querySelector('.post-rail .toc');
                const link = toc.querySelector('.toc__link[aria-current=""true""]');
                const t = toc.getBoundingClientRect();
                const l = link.getBoundingClientRect();
                const panels = [...document.querySelectorAll('.post-rail section.post-rail__panel')];
                const tags = panels.find(s => s.querySelector('.tag-pills')).getBoundingClientRect();
                return JSON.stringify({
                    groupTop: Math.min(tags.top, t.top),
                    groupBottom: Math.max(tags.bottom, t.bottom),
                    tocTop: t.top, tocBottom: t.bottom,
                    linkTop: l.top, linkBottom: l.bottom,
                    scrollHeight: toc.scrollHeight, clientHeight: toc.clientHeight,
                    viewportHeight: window.innerHeight,
                    scrollY: window.scrollY
                });
            }");

        using var doc = JsonDocument.Parse(json!);
        var r = doc.RootElement;
        var groupTop = r.GetProperty("groupTop").GetDouble();
        var groupBottom = r.GetProperty("groupBottom").GetDouble();
        var tocTop = r.GetProperty("tocTop").GetDouble();
        var tocBottom = r.GetProperty("tocBottom").GetDouble();
        var linkTop = r.GetProperty("linkTop").GetDouble();
        var linkBottom = r.GetProperty("linkBottom").GetDouble();
        var scrollHeight = r.GetProperty("scrollHeight").GetDouble();
        var clientHeight = r.GetProperty("clientHeight").GetDouble();
        var viewportHeight = r.GetProperty("viewportHeight").GetDouble();

        Assert.That(groupBottom, Is.LessThanOrEqualTo(viewportHeight - 24 + 1),
            $"the sticky group must be capped at viewport height − 48px so its bottom sits at most {viewportHeight - 24:0.#}px down " +
            $"(top {groupTop:0.#}px, bottom {groupBottom:0.#}px of {viewportHeight:0.#}px).");
        Assert.That(scrollHeight, Is.GreaterThan(clientHeight),
            $"the TOC's own box must scroll inside the capped group (scrollHeight {scrollHeight:0.#} > clientHeight {clientHeight:0.#}).");
        Assert.That(linkTop, Is.GreaterThanOrEqualTo(tocTop - 1),
            $"the scroll-spy's current entry must be scrolled into the TOC's visible box: link top {linkTop:0.#}px, TOC top {tocTop:0.#}px.");
        Assert.That(linkBottom, Is.LessThanOrEqualTo(tocBottom + 1),
            $"the scroll-spy's current entry must be scrolled into the TOC's visible box: link bottom {linkBottom:0.#}px, TOC bottom {tocBottom:0.#}px.");
        Assert.That(r.GetProperty("scrollY").GetDouble(), Is.EqualTo(scrollY).Within(0.5),
            "keeping the current entry visible inside the TOC must not move the page itself.");
    }

    [Test]
    public async Task DraftReview_FeedbackPanelRendersAboveTags_AndTheStickyGroupNeverOverlapsIt()
    {
        // The draft review renders the real post layout with the Feedback panel in the rail;
        // caller panels go above the sticky group so the group can never slide over them.
        await LoginAsync("admin@example.com", "password");
        var title = $"R7D2 Review {RunId}";
        await CreateDraftAsync(title, BodyWithSections(12), Tags());
        await LogoutAsync();
        await LoginAsync("editor@example.com", "password");
        await Page.SetViewportSizeAsync(1440, 900);
        await OpenReviewAsync(title);

        await Page.EvaluateAsync("() => document.fonts.ready");

        var json = await Page.EvaluateAsync<string>(
            @"() => {
                const panels = [...document.querySelectorAll('.post-rail section.post-rail__panel')];
                const feedback = panels.find(s => s.querySelector('.feedback-panel')).getBoundingClientRect();
                const tags = panels.find(s => s.querySelector('.tag-pills')).getBoundingClientRect();
                return JSON.stringify({
                    feedbackTop: feedback.top, feedbackBottom: feedback.bottom,
                    tagsTop: tags.top, tagsBottom: tags.bottom
                });
            }");

        using (var doc = JsonDocument.Parse(json!))
        {
            var r = doc.RootElement;
            var feedbackTop = r.GetProperty("feedbackTop").GetDouble();
            var feedbackBottom = r.GetProperty("feedbackBottom").GetDouble();
            var tagsTop = r.GetProperty("tagsTop").GetDouble();
            var tagsBottom = r.GetProperty("tagsBottom").GetDouble();
            Assert.That(feedbackBottom, Is.LessThanOrEqualTo(tagsTop + 1),
                $"the Feedback panel must render above the Tags panel: feedback bottom {feedbackBottom:0.#}px, tags top {tagsTop:0.#}px (tags bottom {tagsBottom:0.#}px, feedback top {feedbackTop:0.#}px).");
        }

        // Scroll to the end of the page: the sticky group (Tags + TOC) must not overlap the
        // Feedback panel's box.
        await Page.EvaluateAsync("() => window.scrollTo(0, document.documentElement.scrollHeight)");

        json = await Page.EvaluateAsync<string>(
            @"() => {
                const panels = [...document.querySelectorAll('.post-rail section.post-rail__panel')];
                const feedback = panels.find(s => s.querySelector('.feedback-panel')).getBoundingClientRect();
                const tags = panels.find(s => s.querySelector('.tag-pills')).getBoundingClientRect();
                const toc = document.querySelector('.post-rail .toc').getBoundingClientRect();
                return JSON.stringify({
                    feedbackTop: feedback.top, feedbackBottom: feedback.bottom,
                    groupTop: Math.min(tags.top, toc.top), groupBottom: Math.max(tags.bottom, toc.bottom)
                });
            }");

        using (var doc = JsonDocument.Parse(json!))
        {
            var r = doc.RootElement;
            var feedbackTop = r.GetProperty("feedbackTop").GetDouble();
            var feedbackBottom = r.GetProperty("feedbackBottom").GetDouble();
            var groupTop = r.GetProperty("groupTop").GetDouble();
            var groupBottom = r.GetProperty("groupBottom").GetDouble();
            var disjoint = groupTop >= feedbackBottom - 0.5 || groupBottom <= feedbackTop + 0.5;
            Assert.That(disjoint, Is.True,
                $"the sticky group must not overlap the Feedback panel's box at the end of the page: " +
                $"group {groupTop:0.#}..{groupBottom:0.#}px, feedback {feedbackTop:0.#}..{feedbackBottom:0.#}px.");
        }
    }

    [Test]
    public async Task PostPage_At390_InlineTocStaysAboveTheText_AndNoRailElementIsSticky()
    {
        // Below 1000px of content width the rail dissolves: the "On this page" panel stays above
        // the text at load — at rest, no drawer and no pinned row — and no rail element is
        // sticky (computed position).
        var slug = await PublishPostAsync($"R7D2 Phone {RunId}", BodyWithSections(4), Tags());

        await Page.SetViewportSizeAsync(390, 844);
        await Page.GotoAsync($"{BaseUrl}/post/{slug}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.Locator(".toc-inline")).ToBeVisibleAsync();
        await Page.EvaluateAsync("() => document.fonts.ready");

        var json = await Page.EvaluateAsync<string>(
            @"() => {
                const inline = document.querySelector('.toc-inline');
                const box = inline.getBoundingClientRect();
                const body = document.querySelector('.markdown-body').getBoundingClientRect();
                const sticky = [...document.querySelectorAll('.post-rail, .post-rail *')]
                    .filter(el => getComputedStyle(el).position === 'sticky')
                    .map(el => el.tagName.toLowerCase() + '.' + el.className);
                return JSON.stringify({
                    resting: !inline.classList.contains('toc-inline--open')
                        && !inline.classList.contains('toc-inline--pinned'),
                    inlineBottom: box.bottom,
                    bodyTop: body.top,
                    sticky
                });
            }");

        using var doc = JsonDocument.Parse(json!);
        var r = doc.RootElement;
        var sticky = r.GetProperty("sticky").EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.That(r.GetProperty("resting").GetBoolean(), Is.True,
            "the \"On this page\" panel must be at rest at phone width at load — no drawer open and no pinned row.");
        Assert.That(r.GetProperty("inlineBottom").GetDouble(), Is.LessThanOrEqualTo(r.GetProperty("bodyTop").GetDouble() + 1),
            $"the \"On this page\" panel must stay above the text: inline bottom {r.GetProperty("inlineBottom").GetDouble():0.#}px, text top {r.GetProperty("bodyTop").GetDouble():0.#}px.");
        Assert.That(sticky, Is.Empty,
            "no rail element may be sticky below 1000px of content width, found: " + string.Join(", ", sticky));
    }

    /// <summary>Run-keyed tags so the sticky group has its Tags half.</summary>
    private static string[] Tags() => [$"r7d2a{RunId}", $"r7d2b{RunId}", $"r7d2c{RunId}"];

    /// <summary>An intro plus N h2 sections of sentenceRepeats × 26 words (the brief's ~150 words
    /// each at the default 6) — a body tall enough to scroll the post well past the first
    /// screen.</summary>
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

    private async Task LoginAsAdminAsync() => await LoginAsync("admin@example.com", "password");

    private async Task LogoutAsync()
    {
        await Page.GotoAsync($"{BaseUrl}/logout");
        await Expect(Page.GetByText("Log in")).ToBeVisibleAsync();
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

    /// <summary>Opens the draft's review page through the Editor list (EditorTests' pattern: its
    /// Review link carries the slug the public API cannot reveal).</summary>
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
