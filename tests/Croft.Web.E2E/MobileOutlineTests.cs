using System.Text;
using System.Text.Json;

namespace Croft.Web.E2E;

/// <summary>
/// Brief 09e's mobile outline at 390x844 with touch: the "On this page" block pins to one row
/// while reading and, when the reader scrolls back up, grows back into the full list in place —
/// back to the exact height and text position it had at the top, with no frame in which the
/// block covers the post's text. The block's flow footprint (height + margin-bottom) is what
/// keeps everything below it still, so the assertions measure live geometry: the block's height
/// and the first paragraph's top at the top of the page before and after the journey, and the
/// block's box against every in-viewport body paragraph/heading after every scroll step. Each
/// test publishes its own run-keyed post (7 h2 sections of ~260 words). Break-it-once evidence
/// is in the item's commit message.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class MobileOutlineTests : StaticReadingTest
{
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];

    /// <summary>The brief's scroll step and phone viewport, with touch.</summary>
    private const int ScrollStep = 30;

    [Test]
    public async Task PostPage_ScrollingDownThenBackUp_TheBlockReExpandsToItsHeight_AndCoversNoText()
    {
        var slug = await PublishPostAsync($"R9E1 Return {RunId}", BodyWithSections(7), Tags());

        await using var context = await NewPhoneContextAsync();
        var page = await context.NewPageAsync();
        await OpenPostWithPanelAsync(page, slug);

        var initial = await MeasureBlockAndFirstParagraphAsync(page);

        // Down in 30px steps until the block pins (the state class); on the way the block covers
        // nothing — only the pinned row may sit over the page's top 48px, by design.
        var pinned = false;
        for (var y = 0; y < 20000 && !pinned; y += ScrollStep)
        {
            await page.EvaluateAsync("y => window.scrollTo(0, y)", y);
            pinned = await AssertBlockCoversNoTextAsync(page, $"scrolling down to y={y}");
        }

        Assert.That(pinned, Is.True, "the block never pinned while scrolling down.");

        // Back up in 30px steps to scrollY == 0, the same check after every step.
        var cursor = (int)(await page.EvaluateAsync<double>("() => window.scrollY"));
        for (var y = cursor; y > 0; y -= ScrollStep)
        {
            await page.EvaluateAsync("y => window.scrollTo(0, y)", Math.Max(0, y - ScrollStep));
            await AssertBlockCoversNoTextAsync(page, $"scrolling back up to y={Math.Max(0, y - ScrollStep)}");
        }

        await page.EvaluateAsync("() => window.scrollTo(0, 0)");
        await AssertBlockCoversNoTextAsync(page, "back at the top");
        await WaitForBlockSettledAsync(page);

        // Back at the top the block is its own height again and the text sits where it did —
        // the outcome the owner measures: no overlay, no jump.
        var final = await MeasureBlockAndFirstParagraphAsync(page);
        Assert.That(final.BlockHeight, Is.EqualTo(initial.BlockHeight).Within(1),
            $"back at the top the block must be its measured height again (±1px): was {initial.BlockHeight:0.#}px at the start, {final.BlockHeight:0.#}px after the journey.");
        Assert.That(final.FirstParagraphTop, Is.EqualTo(initial.FirstParagraphTop).Within(1),
            $"back at the top the first paragraph must sit where it did (±1px): was {initial.FirstParagraphTop:0.#}px at the start, {final.FirstParagraphTop:0.#}px after the journey.");
    }

    /// <summary>The same journey with the drawer open: opened while pinned, scrolled back to the
    /// top and closed there, the block is back to its height and covers nothing.</summary>
    [Test]
    public async Task PostPage_DrawerOpenWhilePinned_ScrollingBackUpAndClosing_TheBlockReturnsToItsHeight()
    {
        var slug = await PublishPostAsync($"R9E1 Open {RunId}", BodyWithSections(7), Tags());

        await using var context = await NewPhoneContextAsync();
        var page = await context.NewPageAsync();
        await OpenPostWithPanelAsync(page, slug);

        var initial = await MeasureBlockAndFirstParagraphAsync(page);

        // Scroll down until pinned, then open the drawer on the row.
        for (var y = 0; y < 20000; y += ScrollStep)
        {
            await page.EvaluateAsync("y => window.scrollTo(0, y)", y);
            if (await IsPinnedAsync(page))
            {
                break;
            }
        }

        Assert.That(await IsPinnedAsync(page), Is.True, "the block must pin before the drawer is opened.");
        await page.Locator(".toc-inline__row").ClickAsync();
        await Expect(page.Locator(".toc-inline__row")).ToHaveAttributeAsync("aria-expanded", "true");
        await WaitForBlockSettledAsync(page);

        // Scroll back up to the top with the drawer open. The drawer covers the page by design
        // (it floats over a scrim), so the no-coverage check runs once it is closed again.
        var cursor = (int)(await page.EvaluateAsync<double>("() => window.scrollY"));
        for (var y = cursor; y > 0; y -= ScrollStep)
        {
            await page.EvaluateAsync("y => window.scrollTo(0, y)", Math.Max(0, y - ScrollStep));
        }

        await page.EvaluateAsync("() => window.scrollTo(0, 0)");
        await page.Locator(".toc-inline__row").ClickAsync();
        await Expect(page.Locator(".toc-inline__row")).ToHaveAttributeAsync("aria-expanded", "false");
        await WaitForBlockSettledAsync(page);

        await AssertBlockCoversNoTextAsync(page, "after closing at the top");
        var final = await MeasureBlockAndFirstParagraphAsync(page);
        Assert.That(final.BlockHeight, Is.EqualTo(initial.BlockHeight).Within(1),
            $"after the open journey the block must be its measured height again (±1px): was {initial.BlockHeight:0.#}px at the start, {final.BlockHeight:0.#}px at the end.");
        Assert.That(final.FirstParagraphTop, Is.EqualTo(initial.FirstParagraphTop).Within(1),
            $"after the open journey the first paragraph must sit where it did (±1px): was {initial.FirstParagraphTop:0.#}px at the start, {final.FirstParagraphTop:0.#}px at the end.");
    }

    /// <summary>Frame sampling across the pin threshold (item 2): in no frame may the block show
    /// an empty area under the row — whenever the block is more than 8px taller than the pinned
    /// row, the list must be painted (visible, and at least faintly opaque). "Not painted" is
    /// the computed display:none, visibility:hidden or opacity &lt; 0.05 of the list block
    /// (.toc-inline__body — the element the row replaces); display:none is part of it because
    /// that is how the list used to be hidden and the break-it-once restores exactly that. The
    /// traversal samples both crossings — scrolling down pins (the empty box the owner sees is
    /// the shrink with the list already hidden) and scrolling back up unpins (the pop) — in 10px
    /// steps across the band either side of the threshold (SampledBandPx), one DOM sample per animation frame
    /// for 300ms after each step.</summary>
    [Test]
    public async Task PostPage_AcrossThePinThreshold_NoFrameShowsAnEmptyAreaUnderTheRow()
    {
        var slug = await PublishPostAsync($"R9E2 Frames {RunId}", BodyWithSections(7), Tags());

        await using var context = await NewPhoneContextAsync();
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(120000);
        await OpenPostWithPanelAsync(page, slug);

        var threshold = await FindPinThresholdAsync(page);
        var (rowHeight, naturalHeight) = await MeasureRowAndBlockHeightsAsync(page, threshold);
        var result = await SampleFramesAcrossThresholdAsync(page, threshold, rowHeight, naturalHeight, reducedMotion: false);
        var measured = $"(row height {rowHeight:0.#}px, block height {naturalHeight:0.#}px)";

        Assert.That(result.Samples, Is.GreaterThan(500),
            $"the sampler must actually sample frames per animation frame, got {result.Samples} {measured}.");
        Assert.That(result.ViolationCount, Is.Zero,
            $"in no frame may the block be more than 8px taller than the row while the list is not painted " +
            $"({result.ViolationCount} violating frames {measured}, first ones: {string.Join("; ", result.Violations)}).");
        Assert.That(result.IntermediateCount, Is.GreaterThan(0),
            $"the change from row to list must be animated (height and the list's opacity, 200ms): " +
            $"the sampler must catch frames between the two states, got none {measured}.");
    }

    /// <summary>With prefers-reduced-motion the state change must not animate at all: the height
    /// switches from the row's to the block's (and back) in one frame and the list's opacity with
    /// it — no sample may show a height or opacity between the two states.</summary>
    [Test]
    public async Task PostPage_AcrossThePinThreshold_UnderReducedMotion_TheHeightSwitchesInOneFrame()
    {
        var slug = await PublishPostAsync($"R9E2 Reduced {RunId}", BodyWithSections(7), Tags());

        await using var context = await NewPhoneContextAsync();
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(120000);
        await OpenPostWithPanelAsync(page, slug);
        await page.EmulateMediaAsync(new PageEmulateMediaOptions { ReducedMotion = ReducedMotion.Reduce });

        var threshold = await FindPinThresholdAsync(page);
        var (rowHeight, naturalHeight) = await MeasureRowAndBlockHeightsAsync(page, threshold);
        var result = await SampleFramesAcrossThresholdAsync(page, threshold, rowHeight, naturalHeight, reducedMotion: true);
        var measured = $"(row height {rowHeight:0.#}px, block height {naturalHeight:0.#}px)";

        Assert.That(result.Samples, Is.GreaterThan(500),
            $"the sampler must actually sample frames per animation frame, got {result.Samples} {measured}.");
        Assert.That(result.IntermediateCount, Is.Zero,
            $"with reduced motion the state must switch in one frame — every sample is at an endpoint " +
            $"(row's or block's height, list fully faded in or out) " +
            $"({result.IntermediateCount} intermediate frames {measured}, first ones: {string.Join("; ", result.Intermediates)}).");
        await page.EmulateMediaAsync(new PageEmulateMediaOptions { ReducedMotion = ReducedMotion.NoPreference });
    }

    /// <summary>The pinned row's height and the block's natural height, measured live in their
    /// own states (the state machine writes them per state; never hard-coded).</summary>
    private static async Task<(double RowHeight, double NaturalHeight)> MeasureRowAndBlockHeightsAsync(IPage page, int threshold)
    {
        await page.EvaluateAsync("y => window.scrollTo(0, y)", threshold - 300);
        await WaitForBlockSettledAsync(page);
        var natural = await MeasureBlockHeightAsync(page);
        await page.EvaluateAsync("y => window.scrollTo(0, y)", threshold + 300);
        await WaitForBlockSettledAsync(page);
        var row = await MeasureBlockHeightAsync(page);
        return (row, natural);
    }

    private static async Task<double> MeasureBlockHeightAsync(IPage page) =>
        await page.EvaluateAsync<double>("() => document.querySelector('.toc-inline').getBoundingClientRect().height");

    /// <summary>The first scroll position at which the block pins, found by stepping down in 10px
    /// steps until the state class appears.</summary>
    private static async Task<int> FindPinThresholdAsync(IPage page)
    {
        for (var y = 0; y < 20000; y += 10)
        {
            await page.EvaluateAsync("y => window.scrollTo(0, y)", y);
            await WaitForPinStateConsistentAsync(page);
            if (await IsPinnedAsync(page))
            {
                return y;
            }
        }

        Assert.Fail("the block never pinned while scrolling down.");
        return 0;
    }

    /// <summary>
    /// How far either side of the pin threshold the sampler walks. The pin is a pure function of the
    /// scroll position (one threshold, no hysteresis), so the frames that can differ are the ones
    /// around it: the 200 ms transition starts at the threshold and is over a few steps later. The
    /// sampler used to walk 300 px each way (61 steps of 300 ms, about 37 s a test); 100 px each way
    /// is 21 steps and still yields over 700 frames, well above the "really sampling" floor asserted.
    /// </summary>
    private const int SampledBandPx = 100;

    private sealed record FrameSamples(int Samples, int ViolationCount, IReadOnlyList<string> Violations, int IntermediateCount, IReadOnlyList<string> Intermediates);

    /// <summary>One traversal across the pin threshold in 10px steps — down from <see cref="SampledBandPx"/> before it
    /// to the same distance after (the pin) and back up (the unpin) — sampling the block and the list block's
    /// computed paint state every animation frame for 300ms after each step, entirely in-page so
    /// every sample really is a frame.</summary>
    private static async Task<FrameSamples> SampleFramesAcrossThresholdAsync(IPage page, int threshold, double rowHeight, double naturalHeight, bool reducedMotion)
    {
        var from = Math.Max(0, threshold - SampledBandPx);
        var to = threshold + SampledBandPx;

        const string Sampler =
            """
            async ([fromY, toY, rowHeight, naturalHeight, reducedMotion]) => {
                const panel = document.querySelector('.toc-inline');
                const body = panel.querySelector('.toc-inline__body');
                const violations = [];
                const intermediates = [];
                let samples = 0;
                let violationCount = 0;
                let intermediateCount = 0;
                const sampleFor = async ms => {
                    const end = performance.now() + ms;
                    do {
                        await new Promise(resolve => requestAnimationFrame(resolve));
                        const box = panel.getBoundingClientRect();
                        const cs = getComputedStyle(body);
                        const opacity = parseFloat(cs.opacity);
                        samples++;
                        const painted = cs.display !== 'none' && cs.visibility !== 'hidden' && opacity >= 0.05;
                        if (box.height - rowHeight > 8 && !painted) {
                            violationCount++;
                            if (violations.length < 8) {
                                const pcs = getComputedStyle(panel);
                                violations.push('y=' + window.scrollY.toFixed(0) + ' h=' + box.height.toFixed(1)
                                    + ' ' + cs.display + '/' + cs.visibility + '/opacity ' + opacity.toFixed(2)
                                    + ' cls=[' + panel.className + '] inlineH=' + panel.style.height
                                    + ' inlineMb=' + panel.style.marginBottom + ' computedMb=' + pcs.marginBottom
                                    + ' panelTransition=[' + pcs.transition + '] bodyTransition=[' + cs.transition + ']');
                            }
                        }
                        if (Math.abs(box.height - rowHeight) > 1 && Math.abs(box.height - naturalHeight) > 1
                            || opacity > 0.02 && opacity < 0.98) {
                            intermediateCount++;
                            if (intermediates.length < 8) {
                                intermediates.push('y=' + window.scrollY.toFixed(0) + ' h=' + box.height.toFixed(1));
                            }
                        }
                    } while (performance.now() < end);
                };
                const down = fromY < toY;
                for (let y = fromY; down ? y <= toY : y >= toY; y += down ? 10 : -10) {
                    window.scrollTo(0, y);
                    await sampleFor(300);
                }
                return JSON.stringify({ samples, violationCount, violations, intermediateCount, intermediates });
            }
            """;

        var down = await page.EvaluateAsync<string>(Sampler, new object[] { from, to, rowHeight, naturalHeight, reducedMotion });
        var up = await page.EvaluateAsync<string>(Sampler, new object[] { to, from, rowHeight, naturalHeight, reducedMotion });
        using var d = JsonDocument.Parse(down!);
        using var u = JsonDocument.Parse(up!);
        static (int Count, int ViolationCount, List<string> Violations, int IntermediateCount, List<string> Intermediates) Read(JsonElement e) =>
            (e.GetProperty("samples").GetInt32(),
             e.GetProperty("violationCount").GetInt32(),
             [.. e.GetProperty("violations").EnumerateArray().Select(v => v.GetString()!)],
             e.GetProperty("intermediateCount").GetInt32(),
             [.. e.GetProperty("intermediates").EnumerateArray().Select(v => v.GetString()!)]);
        var a = Read(d.RootElement);
        var b = Read(u.RootElement);
        return new FrameSamples(
            a.Count + b.Count,
            a.ViolationCount + b.ViolationCount,
            [.. a.Violations, .. b.Violations],
            a.IntermediateCount + b.IntermediateCount,
            [.. a.Intermediates, .. b.Intermediates]);
    }

    /// <summary>The opened drawer stacks above everything the article draws — on a phone the
    /// post's tag chips used to paint over the drawer's top-right corner. The assertion is the
    /// hit test at that corner (8px inside): the element there must belong to the drawer.</summary>
    [Test]
    public async Task PostPage_OpenedDrawer_TopRightCornerBelongsToTheDrawer_NotTheTags()
    {
        var slug = await PublishPostAsync($"R9E3 Stack {RunId}", BodyWithSections(7), Tags());

        await using var context = await NewPhoneContextAsync();
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(120000);

        foreach (var (width, height) in new[] { (390, 844), (768, 1024) })
        {
            await page.SetViewportSizeAsync(width, height);
            await OpenPostWithPanelAsync(page, slug);

            await page.EvaluateAsync("y => window.scrollTo(0, y)", await FindPinThresholdAsync(page) + 120);
            await WaitForBlockSettledAsync(page);
            await page.Locator(".toc-inline__row").ClickAsync();
            await Expect(page.Locator(".toc-inline__row")).ToHaveAttributeAsync("aria-expanded", "true");
            await WaitForBlockSettledAsync(page);

            var json = await page.EvaluateAsync<string>(
                """
                () => {
                    const panel = document.querySelector('.toc-inline');
                    const box = panel.getBoundingClientRect();
                    const el = document.elementFromPoint(box.right - 8, box.top + 8);
                    return JSON.stringify({
                        inside: el !== null && panel.contains(el),
                        hit: el === null ? 'nothing' : el.tagName.toLowerCase()
                            + (typeof el.className === 'string' && el.className ? '.' + el.className.trim().replace(/\s+/g, '.') : '')
                    });
                }
                """);
            using var doc = JsonDocument.Parse(json!);
            var r = doc.RootElement;
            Assert.That(r.GetProperty("inside").GetBoolean(), Is.True,
                $"at {width}x{height} the element at the opened drawer's top-right corner must belong to the drawer, but the hit test found: {r.GetProperty("hit").GetString()}.");
        }
    }

    /// <summary>The open drawer stacks above the post's tags and everything else the article
    /// draws (brief 09e item 3's "Do"). On this layout the only tag chips at these widths live
    /// inside the drawer's own tags column, so the competition is planted: the post's tag chip
    /// (the probe's #Blazor) and a plain article overlay — real article descendants, outside the
    /// drawer — forced over the drawer's top-right corner at the elevated stacking an article
    /// overlay can reach (z-index 35; the article's own stacking tops out at 3 today). The
    /// assertion is the brief's: the element at the drawer's top-right corner (8px inside)
    /// belongs to the drawer. Dropping the open drawer's stacking rule (ContentsPanel.razor.css)
    /// hands the corner to the planted chip — the probe's mobile-outline-open.png symptom.</summary>
    [Test]
    public async Task PostPage_OpenedDrawer_TopRightCornerBelongsToTheDrawer_AgainstTagsAndArticleOverlays()
    {
        var slug = await PublishPostAsync($"R9E3 Overlay {RunId}", BodyWithSections(7), Tags());

        await using var context = await NewPhoneContextAsync();
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(120000);

        foreach (var (width, height) in new[] { (390, 844), (768, 1024) })
        {
            await page.SetViewportSizeAsync(width, height);
            await OpenPostWithPanelAsync(page, slug);

            await page.EvaluateAsync("y => window.scrollTo(0, y)", await FindPinThresholdAsync(page) + 120);
            await WaitForBlockSettledAsync(page);
            await page.Locator(".toc-inline__row").ClickAsync();
            await Expect(page.Locator(".toc-inline__row")).ToHaveAttributeAsync("aria-expanded", "true");
            await WaitForBlockSettledAsync(page);

            await page.EvaluateAsync(
                """
                () => {
                    const panel = document.querySelector('.toc-inline');
                    const box = panel.getBoundingClientRect();
                    const article = document.querySelector('.markdown-body');
                    const atCorner = 'position:absolute;z-index:35;'
                        + 'top:' + (box.top + window.scrollY + 2) + 'px;'
                        + 'right:' + (window.innerWidth - box.right + 2) + 'px;';
                    const tag = document.createElement('a');
                    tag.className = 'tag-pill';
                    tag.href = '#';
                    tag.textContent = '#probe';
                    tag.style.cssText = atCorner;
                    article.appendChild(tag);
                    const overlay = document.createElement('div');
                    overlay.style.cssText = atCorner + 'width:24px;height:24px;';
                    article.appendChild(overlay);
                }
                """);

            var json = await page.EvaluateAsync<string>(
                """
                () => {
                    const panel = document.querySelector('.toc-inline');
                    const box = panel.getBoundingClientRect();
                    const el = document.elementFromPoint(box.right - 8, box.top + 8);
                    return JSON.stringify({
                        inside: el !== null && panel.contains(el),
                        hit: el === null ? 'nothing' : el.tagName.toLowerCase()
                            + (typeof el.className === 'string' && el.className ? '.' + el.className.trim().replace(/\s+/g, '.') : '')
                    });
                }
                """);
            using var doc = JsonDocument.Parse(json!);
            var r = doc.RootElement;
            Assert.That(r.GetProperty("inside").GetBoolean(), Is.True,
                $"at {width}x{height} the open drawer must stack above the post's tags and the article's overlays, but the hit test at the drawer's top-right corner found: {r.GetProperty("hit").GetString()}.");
        }
    }

    /// <summary>Pulling the outline open works where a reader pulls: anywhere on the pinned row,
    /// not just the handle (brief 09e item 4 — a pull started on the row's text used to scroll
    /// the page instead). Real touch through CDP (Input.dispatchTouchEvent), the brief's test
    /// environment: a vertical pull of ≥ the threshold (180px in 9 steps) on the row's text opens
    /// the drawer, the same pull up closes it, a tap toggles it, and a 3px move changes nothing.</summary>
    [Test]
    public async Task PostPage_PinnedRow_PullOnTheTextOpensAndCloses_TapToggles_ShortMoveDoesNothing()
    {
        var slug = await PublishPostAsync($"R9E4 Pull {RunId}", BodyWithSections(7), Tags());

        await using var context = await NewPhoneContextAsync();
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(120000);
        await OpenPostWithPanelAsync(page, slug);
        await page.EvaluateAsync("y => window.scrollTo(0, y)", await FindPinThresholdAsync(page) + 120);
        await WaitForBlockSettledAsync(page);

        var row = page.Locator(".toc-inline__row");

        // WORKAROUND(W-068, Chromium): Chromium suppresses the synthetic click of a tap that directly follows a synthetic drag, so the tap goes first
        // A tap toggles (first, before any drag: Chromium suppresses the synthetic click of a
        // tap that directly follows a synthetic drag — an artefact of back-to-back CDP touch
        // sequences, not app behaviour).
        await TapOnRowTextAsync(page);
        // END WORKAROUND(W-068)
        await Expect(row).ToHaveAttributeAsync("aria-expanded", "true");
        await WaitForBlockSettledAsync(page);
        await TapOnRowTextAsync(page);
        await Expect(row).ToHaveAttributeAsync("aria-expanded", "false");
        await WaitForBlockSettledAsync(page);

        // A pull down on the row's text opens the drawer.
        await PullOnRowTextAsync(page, 180, 9);
        await Expect(row).ToHaveAttributeAsync("aria-expanded", "true");
        await WaitForBlockSettledAsync(page);

        // The same start, moving up while open, closes it.
        await PullOnRowTextAsync(page, -180, 9);
        await Expect(row).ToHaveAttributeAsync("aria-expanded", "false");
        await WaitForBlockSettledAsync(page);

        // A 3px move is not a pull and not a tap: the drawer stays closed.
        await PullOnRowTextAsync(page, 3, 1);
        await WaitForBlockSettledAsync(page);
        Assert.That(await row.GetAttributeAsync("aria-expanded"), Is.EqualTo("false"),
            "a 3px move on the row's text must change nothing, but the drawer state changed.");
    }

    /// <summary>A touch drag on the row's text through CDP, straight down (negative dy up).</summary>
    private static async Task PullOnRowTextAsync(IPage page, double dy, int steps)
    {
        var cdp = await page.Context.NewCDPSessionAsync(page);
        var box = await page.Locator(".toc-inline__row-text").BoundingBoxAsync();
        var x = box!.X + box.Width / 2;
        var y = box.Y + box.Height / 2;
        await cdp.SendAsync("Input.dispatchTouchEvent", Touch("touchStart", x, y));
        for (var i = 1; i <= steps; i++)
        {
            await cdp.SendAsync("Input.dispatchTouchEvent", Touch("touchMove", x, y + (dy * i / steps)));
        }
        await cdp.SendAsync("Input.dispatchTouchEvent", Touch("touchEnd"));
    }

    /// <summary>A touch tap on the row's text through CDP: down and up at one point.</summary>
    private static async Task TapOnRowTextAsync(IPage page)
    {
        var cdp = await page.Context.NewCDPSessionAsync(page);
        var box = await page.Locator(".toc-inline__row-text").BoundingBoxAsync();
        var x = box!.X + box.Width / 2;
        var y = box.Y + box.Height / 2;
        await cdp.SendAsync("Input.dispatchTouchEvent", Touch("touchStart", x, y));
        await cdp.SendAsync("Input.dispatchTouchEvent", Touch("touchEnd"));
    }

    /// <summary>The CDP arguments for one touch event: one point (none for touchEnd).</summary>
    private static Dictionary<string, object> Touch(string type, double x = 0, double y = 0)
    {
        object[] points = type == "touchEnd" ? [] : [new Dictionary<string, object> { ["x"] = x, ["y"] = y }];
        return new() { ["type"] = type, ["touchPoints"] = points };
    }

    /// <summary>Opens the post on the given page and waits for the enhanced contents panel —
    /// the states are a JavaScript enhancement and the measurements must see the real one.</summary>
    private async Task OpenPostWithPanelAsync(IPage page, string slug)
    {
        await page.GotoAsync($"{BaseUrl}/post/{slug}");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await page.WaitForFunctionAsync("() => document.querySelector('.toc-inline.toc-inline--enhanced') !== null");
        await page.EvaluateAsync("() => document.fonts.ready");
        await WaitForBlockSettledAsync(page);
    }

    /// <summary>Waits for the block's state change to be in place and its ≤200ms transitions
    /// done before geometry is read — concrete conditions (the pin state matching the block's
    /// position, then no running animation), never a sleep. The state is applied on the scroll
    /// handler's frame, so the position check comes first: without it the "no animations" poll
    /// can pass on the frame before the transition even starts.</summary>
    private static async Task WaitForBlockSettledAsync(IPage page)
    {
        await WaitForPinStateConsistentAsync(page);
        await page.WaitForFunctionAsync(
            "() => { const el = document.querySelector('.toc-inline'); return el !== null && el.getAnimations({ subtree: true }).length === 0; }");
    }

    private sealed record BlockGeometry(double BlockHeight, double FirstParagraphTop);

    /// <summary>The block's height and the top of the first body paragraph after it, read in one
    /// pass so the pair always measures one layout.</summary>
    private static async Task<BlockGeometry> MeasureBlockAndFirstParagraphAsync(IPage page)
    {
        var json = await page.EvaluateAsync<string>(
            """
            () => {
                const panel = document.querySelector('.toc-inline').getBoundingClientRect();
                const text = document.querySelector('.markdown-body > p').getBoundingClientRect();
                return JSON.stringify({ blockHeight: panel.height, firstParagraphTop: text.top });
            }
            """);
        using var doc = JsonDocument.Parse(json!);
        return new BlockGeometry(
            doc.RootElement.GetProperty("blockHeight").GetDouble(),
            doc.RootElement.GetProperty("firstParagraphTop").GetDouble());
    }

    /// <summary>Waits for the block's pin state to match its position — the state of this scroll
    /// step, not the one-frame lag behind it (the state is applied on the scroll handler's
    /// frame).</summary>
    private static Task WaitForPinStateConsistentAsync(IPage page) =>
        page.WaitForFunctionAsync(
            """
            () => {
                const el = document.querySelector('.toc-inline');
                return el.classList.contains('toc-inline--pinned') === (el.getBoundingClientRect().top <= 0.5);
            }
            """);

    /// <summary>Asserts the block covers no body paragraph or heading in the viewport — except
    /// while it is pinned, where the row may cover the page's top 48px by design. "Covers" is
    /// the block's painted extent (its box plus any content overflowing it, which paints over
    /// the page exactly like the box does), so an overlay can never hide behind a shrunken box.
    /// The pin state is applied on the scroll handler's frame, so the read waits for it to match
    /// the block's position — the state of the step, not the one-frame lag behind it. Returns
    /// the pinned flag it measured.</summary>
    private static async Task<bool> AssertBlockCoversNoTextAsync(IPage page, string when)
    {
        await WaitForPinStateConsistentAsync(page);
        var json = await page.EvaluateAsync<string>(
            """
            () => {
                const panel = document.querySelector('.toc-inline');
                const box = panel.getBoundingClientRect();
                const coversTo = Math.max(box.bottom, box.top + panel.scrollHeight);
                const covered = [...document.querySelectorAll('.markdown-body p, .markdown-body h2, .markdown-body h3, .markdown-body h4, .markdown-body h5, .markdown-body h6')]
                    .filter(el => {
                        const r = el.getBoundingClientRect();
                        const inViewport = r.bottom > 0 && r.top < window.innerHeight;
                        return inViewport
                            && box.left < r.right && box.right > r.left
                            && box.top < r.bottom && coversTo > r.top;
                    })
                    .map(el => el.tagName.toLowerCase() + ' "' + el.textContent.trim().slice(0, 30)
                        + '" top=' + el.getBoundingClientRect().top.toFixed(1) + ' bottom=' + el.getBoundingClientRect().bottom.toFixed(1));
                return JSON.stringify({
                    pinned: panel.classList.contains('toc-inline--pinned'),
                    open: panel.classList.contains('toc-inline--open'),
                    scrollY: window.scrollY,
                    boxTop: box.top, boxBottom: box.bottom, coversTo,
                    scrollHeight: panel.scrollHeight,
                    inlineHeight: panel.style.height, inlineMarginBottom: panel.style.marginBottom,
                    covered
                });
            }
            """);
        using var doc = JsonDocument.Parse(json!);
        var r = doc.RootElement;
        var pinned = r.GetProperty("pinned").GetBoolean();
        if (!pinned)
        {
            var covered = r.GetProperty("covered").EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.That(covered, Is.Empty,
                $"{when} the block must cover no body paragraph or heading in the viewport, but it covered: {string.Join(", ", covered)} " +
                $"[scrollY={r.GetProperty("scrollY").GetDouble():0.#}, box top={r.GetProperty("boxTop").GetDouble():0.#} bottom={r.GetProperty("boxBottom").GetDouble():0.#}, " +
                $"coversTo={r.GetProperty("coversTo").GetDouble():0.#}, scrollHeight={r.GetProperty("scrollHeight").GetDouble():0.#}, " +
                $"height=\"{r.GetProperty("inlineHeight").GetString()}\", margin-bottom=\"{r.GetProperty("inlineMarginBottom").GetString()}\", " +
                $"open={r.GetProperty("open").GetBoolean()}].");
        }

        return pinned;
    }

    private static async Task<bool> IsPinnedAsync(IPage page) =>
        await page.EvaluateAsync<bool>("() => document.querySelector('.toc-inline').classList.contains('toc-inline--pinned')");

    /// <summary>Run-keyed tags so the panel has its tags column.</summary>
    private static string[] Tags() => [$"r9e1a{RunId}", $"r9e1b{RunId}", $"r9e1c{RunId}"];

    /// <summary>An intro plus N h2 sections of 10 × 26 words — each section long enough to
    /// scroll, so pinning and the journey back have room.</summary>
    private static string BodyWithSections(int sections, int sentenceRepeats = 10)
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

    /// <summary>
    /// Publishes a post through the API as the mock admin (one request; the shape of the post is
    /// what these tests measure, so the editor and its IdP login add nothing but time).
    /// </summary>
    private async Task<string> PublishPostAsync(string title, string body, params IEnumerable<string> tags)
    {
        var published = await PublishAsync(title, body, tags: [.. tags]);
        return published.Slug;
    }
}
