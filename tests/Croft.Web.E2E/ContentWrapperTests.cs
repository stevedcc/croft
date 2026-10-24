namespace Croft.Web.E2E;

/// <summary>
/// Brief 03f item 2: cap and centre the content area. A content wrapper inside <c>main</c>
/// (<c>.shell > main > .content</c>) caps at 80rem and centres (<c>margin-inline: auto</c>), and
/// the post page's text-plus-rail block (the <c>.post-layout</c> grid tracks) is centred under the
/// full-width hero, while item 1's fluid measure (88ch at ≥1920px) and the <c>@container</c> rail
/// switch on <c>main</c> are preserved. Verified through bounding boxes — the outcome the visitor
/// sees — not selector presence.
///
/// Fixture rules (as in FluidTypeTests/PostPageTests): a run-keyed unique post published through the
/// admin UI once per run, shared by every test in the class. Two headings so the rail's TOC renders
/// (and the two-column geometry is exercised at ≥1000px content width) and a tag so the rail has
/// content.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class ContentWrapperTests : WebTest
{
    private static readonly SemaphoreSlim DataGate = new(1, 1);
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];
    private static readonly string Title = $"Wrapper {RunId}";
    private static readonly string TagName = $"wrap{DateTime.UtcNow.Ticks % 1000000:D6}";

    private static bool _dataReady;
    private static string _slug = string.Empty;

    [TestCase(1920)]
    [TestCase(2560)]
    public async Task ContentWrapper_AtBigScreens_IsAtMost80RemAndCentredInMain(int width)
    {
        await EnsureFixtureAsync();
        await Page.SetViewportSizeAsync(width, 900);
        await Page.GotoAsync($"{BaseUrl}/post/{_slug}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.Locator(".markdown-body")).ToBeVisibleAsync();

        var g = await WrapperGeometryAsync();

        // The wrapper caps at 80rem — the value the owner asked for ("cap the content area"). Where
        // main's content box is wider than the cap (both big screens here) the wrapper IS the cap:
        // "at most" alone passes a wrapper capped at 60rem.
        Assert.That(g.MainContentWidth, Is.GreaterThan(g.Rem * 80),
            $"main's content box is {g.MainContentWidth:0.##}px at {width}px: this test needs it wider than the cap to measure the cap.");
        Assert.That(g.WrapperWidth, Is.EqualTo(g.Rem * 80).Within(1),
            $"the content wrapper is {g.WrapperWidth:0.##}px wide, not 80rem ({g.Rem * 80:0.##}px).");

        // Centred in main's content box: the two gaps to main's padding edges are equal.
        Assert.That(Math.Abs(g.LeftGap - g.RightGap), Is.LessThanOrEqualTo(2),
            $"the content wrapper is not centred in main: left gap {g.LeftGap:0.##}px, right gap {g.RightGap:0.##}px.");
    }

    [TestCase(1920)]
    [TestCase(2560)]
    public async Task PostLayout_AtBigScreens_IsCentredUnderHero(int width)
    {
        await EnsureFixtureAsync();
        await Page.SetViewportSizeAsync(width, 900);
        await Page.GotoAsync($"{BaseUrl}/post/{_slug}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.Locator(".post-rail")).ToBeVisibleAsync();
        await Expect(Page.Locator(".markdown-body")).ToBeVisibleAsync();

        var wrapperBox = await Page.Locator("main#content > .content").BoundingBoxAsync();
        var textBox = await Page.Locator(".markdown-body").BoundingBoxAsync();
        var railBox = await Page.Locator(".post-rail").BoundingBoxAsync();
        Assert.That(wrapperBox, Is.Not.Null, "the content wrapper has no bounding box.");
        Assert.That(textBox, Is.Not.Null, "the text column has no bounding box.");
        Assert.That(railBox, Is.Not.Null, "the rail has no bounding box.");

        // The text-plus-rail block (text column's left edge to rail's right edge) must be centred
        // within the wrapper: equal gaps to the wrapper's left and right edges.
        var leftGap = textBox!.X - wrapperBox!.X;
        var rightGap = (wrapperBox.X + wrapperBox.Width) - (railBox!.X + railBox.Width);
        Assert.That(Math.Abs(leftGap - rightGap), Is.LessThanOrEqualTo(2),
            $"the text-plus-rail block is not centred under the hero: left gap {leftGap:0.##}px, right gap {rightGap:0.##}px.");
    }

    [TestCase(1920, 88)]
    [TestCase(2560, 88)]
    public async Task TextColumn_AtBigScreens_StillMeasures88Ch(int width, int expectedCh)
    {
        // Regression guard: item 1's fluid measure must survive the centring. The "ch" of the
        // grid's first track resolves against .post-layout's font (the reading font at the body
        // text's size since brief 03l item 2), so measure that same 1ch before comparing (the
        // FluidTypeTests pattern).
        await EnsureFixtureAsync();
        await Page.SetViewportSizeAsync(width, 900);
        await Page.GotoAsync($"{BaseUrl}/post/{_slug}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.Locator(".markdown-body")).ToBeVisibleAsync();

        await Page.EvaluateAsync("() => document.fonts.ready");
        var measurePx = await Page.Locator(".markdown-body")
            .EvaluateAsync<double>("el => el.getBoundingClientRect().width");
        var chPx = await Page.EvaluateAsync<double>(@"() => {
            const layout = document.querySelector('.post-layout');
            const probe = document.createElement('span');
            probe.style.cssText = 'position:absolute;visibility:hidden;width:1ch;white-space:nowrap;padding:0;margin:0;border:0;';
            layout.appendChild(probe);
            const w = probe.getBoundingClientRect().width;
            probe.remove();
            return w;
        }");

        Assert.That(measurePx, Is.EqualTo(expectedCh * chPx).Within(chPx),
            $"the text column at {width}px should stay {expectedCh}ch ({expectedCh * chPx:0.##}px), was {measurePx:0.##}px (1ch = {chPx:0.##}px).");
    }

    [TestCase(1440)]
    [TestCase(390)]
    public async Task ContentWrapper_AtOrBelow1440_IsFullWidth(int width)
    {
        // Below 80rem the wrapper has no effect: it spans main's content box edge to edge.
        await EnsureFixtureAsync();
        await Page.SetViewportSizeAsync(width, 900);
        await Page.GotoAsync($"{BaseUrl}/post/{_slug}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.Locator(".markdown-body")).ToBeVisibleAsync();

        var g = await WrapperGeometryAsync();

        Assert.That(Math.Abs(g.WrapperWidth - g.MainContentWidth), Is.LessThanOrEqualTo(1),
            $"the content wrapper at {width}px should span main's content box ({g.MainContentWidth:0.##}px), was {g.WrapperWidth:0.##}px.");
    }

    [TestCase(768)]
    [TestCase(390)]
    public async Task TextColumn_Below1000pxContent_IsLeftAlignedWithinWrapper(int width)
    {
        // Locks the "phones and small tablets must not change" constraint: the text-plus-rail
        // block is only centred once main's content area reaches 1000px (brief 03l item 2
        // replaced 03f's 80rem gate; the 1440px case this test used to carry is a centring case
        // now and is covered by PostLayout_AtBigScreens_IsCentredUnderHero and
        // ReadingMeasureTests.TextPlusRail_IsCentredUnderTheHero). Below the switch the wrapper
        // is full-width and the block stays left-aligned — its left edge at the wrapper's
        // content left. The assertion is unchanged from 03f; only the input widths moved.
        await EnsureFixtureAsync();
        await Page.SetViewportSizeAsync(width, 900);
        await Page.GotoAsync($"{BaseUrl}/post/{_slug}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.Locator(".markdown-body")).ToBeVisibleAsync();

        var wrapperBox = await Page.Locator("main#content > .content").BoundingBoxAsync();
        var textBox = await Page.Locator(".markdown-body").BoundingBoxAsync();
        Assert.That(wrapperBox, Is.Not.Null, "the content wrapper has no bounding box.");
        Assert.That(textBox, Is.Not.Null, "the text column has no bounding box.");

        Assert.That(textBox!.X, Is.EqualTo(wrapperBox!.X).Within(2),
            $"the text column at {width}px should be left-aligned within the wrapper (text x {textBox.X:0.##} vs wrapper x {wrapperBox.X:0.##}).");
    }

    /// <summary>The wrapper's geometry relative to main: its width, main's content-box width (the
    /// box the wrapper centres in), the root font size (80rem = 80 × rem) and the two gaps between
    /// the wrapper and main's padding edges (equal when centred).</summary>
    private async Task<(double WrapperWidth, double MainContentWidth, double Rem, double LeftGap, double RightGap)> WrapperGeometryAsync()
    {
        var main = Page.Locator("main#content");
        var wrapper = Page.Locator("main#content > .content");
        await main.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });
        await wrapper.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });

        var mainBox = await main.BoundingBoxAsync();
        var wrapperBox = await wrapper.BoundingBoxAsync();
        Assert.That(mainBox, Is.Not.Null, "main has no bounding box.");
        Assert.That(wrapperBox, Is.Not.Null, "the content wrapper has no bounding box.");

        var padLeft = await main.EvaluateAsync<double>("el => parseFloat(getComputedStyle(el).paddingLeft)");
        var padRight = await main.EvaluateAsync<double>("el => parseFloat(getComputedStyle(el).paddingRight)");
        var rem = await Page.EvaluateAsync<double>(
            "() => parseFloat(getComputedStyle(document.documentElement).fontSize)");

        return (
            wrapperBox!.Width,
            mainBox!.Width - padLeft - padRight,
            rem,
            wrapperBox.X - (mainBox.X + padLeft),
            (mainBox.X + mainBox.Width - padRight) - (wrapperBox.X + wrapperBox.Width));
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

            await LoginAsync("admin@example.com", "password");

            await OpenNewPostEditorAsync();

            // Two headings so the rail's TOC renders (and the two-column geometry is exercised at
            // ≥1000px content width), a tag so the rail has content, and enough body text.
            var body =
                $"Fixture intro {RunId}.\n\n" +
                "## First section\n\n" +
                $"First section body {RunId}. The measure of this column is the outcome under test.\n\n" +
                "## Second section\n\n" +
                $"Second section body {RunId} with enough words to fill the line comfortably.";

            await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync(Title);
            await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).BlurAsync();
            await Page.GetByLabel("Markdown").FillAsync(body);
            await Page.GetByLabel("Markdown").BlurAsync();
            await AddTagsAsync(TagName);

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
