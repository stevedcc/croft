namespace Croft.Web.E2E;

/// <summary>
/// Brief 03f item 1: fluid type and a longer line above 1440px. The root (html) font size grows
/// linearly from 16px at 1440px to 19px at 2560px, capped at both ends (one clamp()), and the
/// post text column grows from 68ch at 1440px to 88ch at 1920px, also capped. Viewports ≤1440px
/// (including phones) keep 16px / 68ch. Verified through computed styles: the outcome the visitor
/// sees (the rendered column width and the cascade root's font size), not selector presence.
///
/// Fixture rules (as in PostPageTests/ThemeTests): a run-keyed unique post published through the
/// admin UI once per run, shared by every test in the class. The 68ch/88ch measure is the grid's
/// first track, so the assertion compares the .markdown-body bounding box against the same font
/// context the grid resolves "ch" in (the .post-layout container — the reading font at the body
/// text's size since brief 03l item 2) — measured with a 1ch probe.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class FluidTypeTests : WebTest
{
    private static readonly SemaphoreSlim DataGate = new(1, 1);
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];
    private static readonly string Title = $"Fluid {RunId}";
    private static readonly string TagName = $"fluid{DateTime.UtcNow.Ticks % 1000000:D6}";

    private static bool _dataReady;
    private static string _slug = string.Empty;

    [TestCase(390)]
    [TestCase(1440)]
    public async Task RootFontSize_AtOrBelow1440_Is16px(int width)
    {
        await AssertRootFontSizeAsync(width, expected: 16.0, tolerance: 0.5);
    }

    [TestCase(2560)]
    [TestCase(3000)]
    public async Task RootFontSize_AtOrAbove2560_Is19px(int width)
    {
        await AssertRootFontSizeAsync(width, expected: 19.0, tolerance: 0.5);
    }

    [Test]
    public async Task RootFontSize_At1920_IsStrictlyBetween16And19()
    {
        var px = await MeasureRootFontSizeAsync(1920);
        Assert.That(px, Is.GreaterThan(16.0).And.LessThan(19.0),
            $"root font size at 1920px must be strictly between 16px and 19px, was {px:0.####}px.");
    }

    [TestCase(1440, 68, 0.5)]
    [TestCase(1920, 88, 1.0)]
    [TestCase(2560, 88, 1.0)]
    public async Task TextColumn_AtViewport_IsExpectedCh(int width, int expectedCh, double toleranceCh)
    {
        await EnsureFixtureAsync();
        await Page.SetViewportSizeAsync(width, 900);
        await Page.GotoAsync($"{BaseUrl}/post/{_slug}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.Locator(".markdown-body")).ToBeVisibleAsync();

        // The "ch" of the grid's first track is resolved against .post-layout's font (the reading
        // font at the body text's size), so measure that same 1ch before comparing.
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

        Assert.That(measurePx, Is.EqualTo(expectedCh * chPx).Within(toleranceCh * chPx),
            $"the text column at {width}px should be {expectedCh}ch ({expectedCh * chPx:0.##}px), was {measurePx:0.##}px (1ch = {chPx:0.##}px).");
    }

    [TestCase(1440)]
    public async Task MarkdownBody_At1440_Is18px(int width)
    {
        await EnsureFixtureAsync();
        await Page.SetViewportSizeAsync(width, 900);
        await Page.GotoAsync($"{BaseUrl}/post/{_slug}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.Locator(".markdown-body")).ToBeVisibleAsync();
        await Page.EvaluateAsync("() => document.fonts.ready");

        var px = await Page.Locator(".markdown-body")
            .EvaluateAsync<double>("el => parseFloat(getComputedStyle(el).fontSize)");
        Assert.That(px, Is.EqualTo(18.0).Within(0.5),
            $"the post body at {width}px should be 18px (1.125rem × 16px root), was {px:0.####}px.");
    }

    [TestCase(2560)]
    public async Task MarkdownBody_At2560_IsLargerThan18px(int width)
    {
        // Brief 03f item 5 (owner ruling): the reading text must grow with the fluid root. At
        // 2560px the root is 19px, so 1.125rem resolves to 21.375px — strictly larger than the
        // 18px it had before the conversion (the RED/break-it-once case).
        await EnsureFixtureAsync();
        await Page.SetViewportSizeAsync(width, 900);
        await Page.GotoAsync($"{BaseUrl}/post/{_slug}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.Locator(".markdown-body")).ToBeVisibleAsync();
        await Page.EvaluateAsync("() => document.fonts.ready");

        var px = await Page.Locator(".markdown-body")
            .EvaluateAsync<double>("el => parseFloat(getComputedStyle(el).fontSize)");
        Assert.That(px, Is.GreaterThan(18.0),
            $"the post body at {width}px must be larger than 18px, was {px:0.####}px.");
        Assert.That(px, Is.EqualTo(21.375).Within(0.5),
            $"the post body at {width}px should be 21.375px (1.125rem × 19px root), was {px:0.####}px.");
    }

    private async Task AssertRootFontSizeAsync(int width, double expected, double tolerance)
    {
        var px = await MeasureRootFontSizeAsync(width);
        Assert.That(px, Is.EqualTo(expected).Within(tolerance),
            $"root font size at {width}px should be {expected}px, was {px:0.####}px.");
    }

    /// <summary>Reads the computed root (html) font size at the given viewport width. The value is
    /// global (app.css's html clamp), so the home page is enough; no fixture is needed.</summary>
    private async Task<double> MeasureRootFontSizeAsync(int width)
    {
        await Page.SetViewportSizeAsync(width, 900);
        await Page.GotoAsync(BaseUrl);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        return await Page.EvaluateAsync<double>(
            "() => parseFloat(getComputedStyle(document.documentElement).fontSize)");
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

            await Page.GotoAsync($"{BaseUrl}/logout");
            _dataReady = true;
        }
        finally
        {
            DataGate.Release();
        }
    }
}
