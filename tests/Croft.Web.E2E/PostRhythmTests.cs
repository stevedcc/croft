using System.Text.Json;

namespace Croft.Web.E2E;

/// <summary>
/// The post body's vertical rhythm: one em-based spacing scale throughout .markdown-body, so
/// separate paragraphs no longer run together, list items read like lines of text (no extra gap
/// between them), headings breathe more above than below, and the body's first child starts at
/// its top edge (its last child ends at the bottom). The units are em so the gaps follow the body
/// font (1.125rem, 1.0625rem below 960px), not a fixed rem.
///
/// Outcomes asserted at 390px and 1440px, measured in the browser (rects and computed styles):
/// the gap between the two paragraphs equals one body em within 1px; consecutive list items are
/// one line-height apart within 1px (no extra gap); the gap above the h2 is larger than the gap
/// below it; the first element starts at the body's top and the last ends at its bottom (within
/// 1px — .markdown-body is a grid item, so its children's margins stay inside its box and the
/// edges are observable; MeasureAsync pins that box to its content first, so the grid row's
/// stretch cannot add slack below the last child). Break-it-once: restoring li + li { margin-top:
/// .35rem } pushes the items 5.6px apart at a 16px root and fails the one-line-height assertion.
///
/// Fixtures (the suite's rule): a run-keyed unique post published through the admin UI once per
/// run, shared by every test in the class: two paragraphs, an h2, a tight list of three
/// single-line items (the third wrapping a nested list), an h3 and a code block — the same body
/// the post-rhythm screenshot captures show.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class PostRhythmTests : WebTest
{
    private static readonly SemaphoreSlim DataGate = new(1, 1);
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];
    private static readonly string Title = $"Rhythm {RunId}";

    private static bool _dataReady;
    private static string _slug = string.Empty;

    [TestCase(390)]
    [TestCase(1440)]
    public async Task Paragraphs_AreOneBodyEmApart(int width)
    {
        var m = await MeasureAsync(width);

        Assert.That(m.ParaGap, Is.EqualTo(m.BodyFontPx).Within(1),
            $"the gap between the two paragraphs at {width}px should be one body em ({m.BodyFontPx:0.##}px), " +
            $"was {m.ParaGap:0.##}px.");
    }

    [TestCase(390)]
    [TestCase(1440)]
    public async Task ListItems_AreOneLineHeightApart(int width)
    {
        var m = await MeasureAsync(width);

        Assert.That(m.ItemSteps, Is.Not.Empty, "the fixture post has no consecutive top-level list items.");
        for (var i = 0; i < m.ItemSteps.Count; i++)
        {
            Assert.That(m.ItemSteps[i], Is.EqualTo(m.LineHeight).Within(1),
                $"list item {i + 2} at {width}px starts {m.ItemSteps[i]:0.##}px below item {i + 1} " +
                $"(one line-height is {m.LineHeight:0.##}px); consecutive items must read like lines of text.");
            Assert.That(m.ItemGaps[i], Is.EqualTo(0).Within(1),
                $"list items {i + 1} and {i + 2} at {width}px are {m.ItemGaps[i]:0.##}px apart; " +
                "a tight list has no extra space between its items.");
        }
    }

    [TestCase(390)]
    [TestCase(1440)]
    public async Task H2_HasMoreSpaceAboveThanBelow(int width)
    {
        var m = await MeasureAsync(width);

        Assert.That(m.H2GapAbove, Is.GreaterThan(m.H2GapBelow),
            $"the h2 at {width}px has {m.H2GapAbove:0.##}px of space above and {m.H2GapBelow:0.##}px below; " +
            "a heading must breathe more above than below.");
    }

    [TestCase(390)]
    [TestCase(1440)]
    public async Task BodyEdges_StayFlush(int width)
    {
        var m = await MeasureAsync(width);

        Assert.That(m.FirstTopGap, Is.EqualTo(0).Within(1),
            $"the body's first element at {width}px starts {m.FirstTopGap:0.##}px below the body's top; " +
            "the first child must have no top margin.");
        Assert.That(m.LastBottomGap, Is.EqualTo(0).Within(1),
            $"the body's last element at {width}px ends {m.LastBottomGap:0.##}px above the body's bottom; " +
            "the last child must have no bottom margin.");
    }

    /// <summary>One page's rhythm geometry: the paragraph gap and the body font size that resolves
    /// its em, the top-to-top step and border-box gap between consecutive top-level list items and
    /// the line-height they must equal, the h2's gaps, and the body's edge gaps (first child's top
    /// margin, last child's bottom margin — contained in the box because .markdown-body is a grid
    /// item, so they are observable in the rects once the harness pins the box to its content).</summary>
    private sealed record RhythmGeometry(
        double ParaGap,
        double BodyFontPx,
        double LineHeight,
        IReadOnlyList<double> ItemSteps,
        IReadOnlyList<double> ItemGaps,
        double H2GapAbove,
        double H2GapBelow,
        double FirstTopGap,
        double LastBottomGap);

    private async Task<RhythmGeometry> MeasureAsync(int width)
    {
        await EnsureFixtureAsync();
        await Page.SetViewportSizeAsync(width, 900);
        await Page.GotoAsync($"{BaseUrl}/post/{_slug}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.Locator(".markdown-body")).ToBeVisibleAsync();
        await Page.EvaluateAsync("() => document.fonts.ready");

        // Measurement harness: .markdown-body is a grid item in .post-layout (display: grid,
        // default align-items: stretch), so whenever the rail (TOC + related-post back-fill) is
        // taller than the body the body's box stretches to its row and the space below the last
        // child is row slack, not that child's margin — yet the edges asserted here are the body's
        // children's margins. Pinning the body's box to its content (align-self: start) makes the
        // box end where its content ends, so the edge gaps measure the margins again; the
        // rail-dependent stretch is pre-existing layout outside this item's scope and is not what
        // these assertions cover. Child-to-child gaps (paragraphs, list items, headings) are
        // unaffected by the stretch and by this pin either way. The pin goes in through CSSOM
        // (element.style), not an injected style element: the enforced policy blocks inline
        // style elements on these pages, while CSSOM assignments are outside style-src-attr and
        // style-src-elem — the same mechanism the app's own Add-feedback button uses.
        await Page.EvaluateAsync("() => { document.querySelector('.markdown-body').style.alignSelf = 'start'; }");

        // The fixture must actually render the rhythm shapes before any geometry means anything.
        await Expect(Page.Locator(".markdown-body > p")).ToHaveCountAsync(2);
        await Expect(Page.Locator(".markdown-body > h2")).ToHaveCountAsync(1);
        await Expect(Page.Locator(".markdown-body > ul > li")).ToHaveCountAsync(3);
        await Expect(Page.Locator(".markdown-body > ul > li > ul > li")).ToHaveCountAsync(2);
        await Expect(Page.Locator(".markdown-body > h3")).ToHaveCountAsync(1);
        await Expect(Page.Locator(".markdown-body > pre")).ToHaveCountAsync(1);

        var json = await Page.EvaluateAsync<string>(
            @"() => {
                const body = document.querySelector('.markdown-body');
                const bodyRect = body.getBoundingClientRect();
                const ps = [...body.querySelectorAll(':scope > p')];
                const h2 = body.querySelector(':scope > h2');
                const ul = body.querySelector(':scope > ul');
                const items = [...ul.querySelectorAll(':scope > li')];
                const rect = el => el.getBoundingClientRect();

                return JSON.stringify({
                    paraGap: rect(ps[1]).top - rect(ps[0]).bottom,
                    bodyFontPx: parseFloat(getComputedStyle(ps[0]).fontSize),
                    lineHeight: parseFloat(getComputedStyle(items[0]).lineHeight),
                    itemTops: items.map(el => rect(el).top),
                    itemBottoms: items.map(el => rect(el).bottom),
                    h2GapAbove: rect(h2).top - rect(ps[1]).bottom,
                    h2GapBelow: rect(ul).top - rect(h2).bottom,
                    firstTopGap: rect(body.firstElementChild).top - bodyRect.top,
                    lastBottomGap: bodyRect.bottom - rect(body.lastElementChild).bottom
                });
            }");

        using var doc = JsonDocument.Parse(json!);
        var r = doc.RootElement;
        var tops = r.GetProperty("itemTops").EnumerateArray().Select(e => e.GetDouble()).ToList();
        var bottoms = r.GetProperty("itemBottoms").EnumerateArray().Select(e => e.GetDouble()).ToList();

        // The step and gap of each consecutive pair; with the nested list under the last item,
        // every earlier item is single-line, so the step is exactly one line of text.
        List<double> steps = [];
        List<double> gaps = [];
        for (var i = 0; i + 1 < tops.Count; i++)
        {
            steps.Add(tops[i + 1] - tops[i]);
            gaps.Add(tops[i + 1] - bottoms[i]);
        }

        return new RhythmGeometry(
            r.GetProperty("paraGap").GetDouble(),
            r.GetProperty("bodyFontPx").GetDouble(),
            r.GetProperty("lineHeight").GetDouble(),
            steps,
            gaps,
            r.GetProperty("h2GapAbove").GetDouble(),
            r.GetProperty("h2GapBelow").GetDouble(),
            r.GetProperty("firstTopGap").GetDouble(),
            r.GetProperty("lastBottomGap").GetDouble());
    }

    /// <summary>The rhythm fixture body: two paragraphs, an h2, a tight list of three single-line
    /// items (the third wrapping a nested list), an h3 and a code block.</summary>
    private static string RhythmBody() =>
        $"Rhythm fixture {RunId}, the first of two paragraphs whose spacing sets the reading rhythm.\n\n" +
        $"Second paragraph {RunId}, separated from the first by exactly one body em.\n\n" +
        "## Rhythm section\n\n" +
        "- Alpha item\n" +
        "- Beta item\n" +
        "- Gamma item\n" +
        "  - Nested one\n" +
        "  - Nested two\n\n" +
        "### Rhythm sub-section\n\n" +
        "```text\n" +
        "var rhythm = 1;\n" +
        "```";

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

            await LoginAsync("admin@example.com", "password");

            await OpenNewPostEditorAsync();

            await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync(Title);
            await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).BlurAsync();
            await Page.GetByLabel("Markdown").FillAsync(RhythmBody());
            await Page.GetByLabel("Markdown").BlurAsync();

            await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Publish now" }).ClickAsync();
            await Page.WaitForURLAsync(
                url => url.StartsWith($"{BaseUrl}/admin/posts/") && url.EndsWith("/edit"),
                new PageWaitForURLOptions { Timeout = 30000 });

            _slug = await SlugForPostAsync(PostIdFromEditorUrl());

            await SignOutAsync();
            _dataReady = true;
        }
        finally
        {
            DataGate.Release();
        }
    }
}
