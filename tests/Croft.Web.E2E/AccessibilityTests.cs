using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;

namespace Croft.Web.E2E;

/// <summary>
/// Task R3.6: the accessibility and overflow pass over the rebuilt public pages. Task R5.2 extends
/// the axe runs to the editing surfaces: the admin posts list, the post editor, and the Editor
/// role's list and draft review (ruling R5-2-b's superset reading of "the Editor and Posts list").
///
/// Axe (Deque.AxeCore.Playwright) runs on every surface in light and dark; serious/critical
/// violations fail the test, lower severities are logged only. Dark mode comes from the stored
/// blog-theme=dark preference (spec §4.3: MudBlazor-styled controls may flash light on first load,
/// so each dark test waits for the rendered dark state the user sees — the same palette sentinel
/// ThemeTests uses — never a fixed delay).
///
/// NoHorizontalOverflow_At390 checks Review Focus 2 at phone width on Home and on a Post with a
/// 300-char title and 20 tags: document.documentElement.scrollWidth must fit the 390px viewport.
/// The card computed-style test is the behavioural backstop for CardCssContractTests' CSS-text
/// pins (ruling R3-6-2, Task R3.2's deferred minor).
///
/// Fixture rules (brief hand-off + R3.3's): the shared dev DB accumulates posts across runs, so the
/// fixture post is unique per run (per-run id in the title and in all 20 tag names) and created by
/// this class before anything counts on it. Assertions use Playwright's retrying expects and waits
/// on concrete conditions (R3.3's flake guidance: the prerender → interactive DOM swap can detach
/// nodes seen by one-shot reads).
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class AccessibilityTests : WebTest
{
    private static readonly SemaphoreSlim DataGate = new(1, 1);
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];

    /// <summary>Exactly 300 characters (Review Focus 2), with the run id up front so the post is identifiable.</summary>
    private static readonly string LongTitle = BuildLongTitle();

    private static readonly string TagList =
        string.Join(", ", Enumerable.Range(1, 20).Select(n => $"r36{RunId}t{n:00}"));

    private static bool _dataReady;
    private static string _longSlug = string.Empty;

    /// <summary>The run-keyed draft the editing and Editor-role scans review. Every optional field
    /// is missing by construction (no category, banner, subtitle or tags) so those surfaces render
    /// the banner-less fallback and the no-chip/no-pill shapes axe must pass on (Review Focus 1).</summary>
    private static readonly string DraftTitle = $"R52 Review {RunId}";

    private static bool _draftReady;
    private static Guid _draftId;
    private static string _draftSlug = string.Empty;

    /// <summary>Compact-row bound for List cards: mockup post-cards option D's rows are ~110–150px
    /// at 1440px wide, so 160px allows a wrapped text line without letting the banner drive the row.</summary>
    private const double MaxCompactRowHeight = 160;

    /// <summary>Identifies this run's list-row banner fixtures. Deliberately not <see cref="RunId"/>:
    /// the search for RunId above must still return exactly the one fixture post it counts.</summary>
    private static readonly string RowToken = "rowfix" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>Three banner fixtures with identical text: square, portrait and landscape — the
    /// intrinsic aspects a List row must crop to the same compact shape. Title suffixes share a
    /// width so every fixture row is text-identical.</summary>
    private static readonly (string Suffix, int Width, int Height)[] BannerFixtures =
    [
        ("banner one", 400, 400),
        ("banner two", 240, 480),
        ("banner six", 480, 240),
    ];

    private static bool _rowDataReady;

    [Test]
    public async Task HomePage_Light_NoSeriousOrCriticalAxeViolations()
    {
        await EnsureFixturesAsync();
        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ColorScheme = ColorScheme.Light,
            ViewportSize = new() { Width = 1440, Height = 900 }
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync(BaseUrl);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Latest Posts" }))
            .ToBeVisibleAsync();

        await AssertNoSeriousOrCriticalAxeViolationsAsync(page);
    }

    [Test]
    public async Task HomePage_Dark_NoSeriousOrCriticalAxeViolations()
    {
        await EnsureFixturesAsync();
        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ColorScheme = ColorScheme.Light,
            ViewportSize = new() { Width = 1440, Height = 900 }
        });
        await context.AddInitScriptAsync("localStorage.setItem('blog-theme', 'dark')");
        var page = await context.NewPageAsync();
        await page.GotoAsync(BaseUrl);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Latest Posts" }))
            .ToBeVisibleAsync();
        await WaitForDarkPaletteAsync(page);

        await AssertNoSeriousOrCriticalAxeViolationsAsync(page);
    }

    [Test]
    public async Task PostPage_Light_NoSeriousOrCriticalAxeViolations()
    {
        await EnsureFixturesAsync();
        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ColorScheme = ColorScheme.Light,
            ViewportSize = new() { Width = 1440, Height = 900 }
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/post/{_longSlug}");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await WaitForPostRenderedAsync(page);

        await AssertNoSeriousOrCriticalAxeViolationsAsync(page);
    }

    [Test]
    public async Task PostPage_Dark_NoSeriousOrCriticalAxeViolations()
    {
        await EnsureFixturesAsync();
        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ColorScheme = ColorScheme.Light,
            ViewportSize = new() { Width = 1440, Height = 900 }
        });
        await context.AddInitScriptAsync("localStorage.setItem('blog-theme', 'dark')");
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/post/{_longSlug}");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await WaitForPostRenderedAsync(page);
        await WaitForDarkPaletteAsync(page);

        await AssertNoSeriousOrCriticalAxeViolationsAsync(page);
    }

    [Test]
    public async Task PostsList_Light_NoSeriousOrCriticalAxeViolations()
    {
        await EnsureDraftFixtureAsync();
        await Page.SetViewportSizeAsync(1440, 900);
        await LoginAsync("admin@example.com", "password");
        await Page.GotoAsync($"{BaseUrl}/admin/posts");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Posts", Level = 1 })).ToBeVisibleAsync();
        // The Draft filter is how the fixture row reaches page 1: the shared dev DB holds posts
        // scheduled far in the future, which sort above fresh rows on the unfiltered list (the
        // suite's fixture rule). The filtered grid is still the whole list surface — chips,
        // search, rows and pager all render and get scanned.
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Draft", Exact = true }).ClickAsync();
        await Expect(Page.Locator(".posts-grid__row", new PageLocatorOptions { HasText = DraftTitle })).ToBeVisibleAsync();

        await AssertNoSeriousOrCriticalAxeViolationsAsync(Page);
    }

    [Test]
    public async Task PostsList_Dark_NoSeriousOrCriticalAxeViolations()
    {
        await Page.Context.AddInitScriptAsync("localStorage.setItem('blog-theme', 'dark')");
        await EnsureDraftFixtureAsync();
        await Page.SetViewportSizeAsync(1440, 900);
        await LoginAsync("admin@example.com", "password");
        await Page.GotoAsync($"{BaseUrl}/admin/posts");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Posts", Level = 1 })).ToBeVisibleAsync();
        // As in the light scan: the Draft filter brings the fixture row to page 1.
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Draft", Exact = true }).ClickAsync();
        await Expect(Page.Locator(".posts-grid__row", new PageLocatorOptions { HasText = DraftTitle })).ToBeVisibleAsync();
        await WaitForDarkPaletteAsync(Page);

        await AssertNoSeriousOrCriticalAxeViolationsAsync(Page);
    }

    [Test]
    public async Task PostEditor_Light_NoSeriousOrCriticalAxeViolations()
    {
        await EnsureDraftFixtureAsync();
        await Page.SetViewportSizeAsync(1440, 900);
        await LoginAsync("admin@example.com", "password");
        await Page.GotoAsync($"{BaseUrl}/admin/posts/{_draftId}/edit");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Edit Post", Level = 1 })).ToBeVisibleAsync();
        await Expect(Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true })).ToHaveValueAsync(DraftTitle);

        await AssertNoSeriousOrCriticalAxeViolationsAsync(Page);
    }

    [Test]
    public async Task PostEditor_Dark_NoSeriousOrCriticalAxeViolations()
    {
        await Page.Context.AddInitScriptAsync("localStorage.setItem('blog-theme', 'dark')");
        await EnsureDraftFixtureAsync();
        await Page.SetViewportSizeAsync(1440, 900);
        await LoginAsync("admin@example.com", "password");
        await Page.GotoAsync($"{BaseUrl}/admin/posts/{_draftId}/edit");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Edit Post", Level = 1 })).ToBeVisibleAsync();
        await Expect(Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true })).ToHaveValueAsync(DraftTitle);
        await WaitForDarkPaletteAsync(Page);

        await AssertNoSeriousOrCriticalAxeViolationsAsync(Page);
    }

    [Test]
    public async Task EditorList_Light_NoSeriousOrCriticalAxeViolations()
    {
        await EnsureDraftFixtureAsync();
        await Page.SetViewportSizeAsync(1440, 900);
        await LoginAsync("editor@example.com", "password");
        await Page.GotoAsync($"{BaseUrl}/editor");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Drafts", Level = 1 })).ToBeVisibleAsync();
        await Expect(Page.Locator(".posts-grid__row", new PageLocatorOptions { HasText = DraftTitle })).ToBeVisibleAsync();

        await AssertNoSeriousOrCriticalAxeViolationsAsync(Page);
    }

    [Test]
    public async Task EditorList_Dark_NoSeriousOrCriticalAxeViolations()
    {
        await Page.Context.AddInitScriptAsync("localStorage.setItem('blog-theme', 'dark')");
        await EnsureDraftFixtureAsync();
        await Page.SetViewportSizeAsync(1440, 900);
        await LoginAsync("editor@example.com", "password");
        await Page.GotoAsync($"{BaseUrl}/editor");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Drafts", Level = 1 })).ToBeVisibleAsync();
        await Expect(Page.Locator(".posts-grid__row", new PageLocatorOptions { HasText = DraftTitle })).ToBeVisibleAsync();
        await WaitForDarkPaletteAsync(Page);

        await AssertNoSeriousOrCriticalAxeViolationsAsync(Page);
    }

    [Test]
    public async Task EditorReview_Light_NoSeriousOrCriticalAxeViolations()
    {
        await EnsureDraftFixtureAsync();
        await Page.SetViewportSizeAsync(1440, 900);
        await LoginAsync("editor@example.com", "password");
        await Page.GotoAsync($"{BaseUrl}/editor/posts/{_draftSlug}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.Locator(".post-hero__ribbon")).ToHaveTextAsync("Draft");
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = DraftTitle, Level = 1 })).ToBeVisibleAsync();
        await Expect(Page.Locator(".markdown-body")).ToBeVisibleAsync();

        await AssertNoSeriousOrCriticalAxeViolationsAsync(Page);
    }

    [Test]
    public async Task EditorReview_Dark_NoSeriousOrCriticalAxeViolations()
    {
        await Page.Context.AddInitScriptAsync("localStorage.setItem('blog-theme', 'dark')");
        await EnsureDraftFixtureAsync();
        await Page.SetViewportSizeAsync(1440, 900);
        await LoginAsync("editor@example.com", "password");
        await Page.GotoAsync($"{BaseUrl}/editor/posts/{_draftSlug}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.Locator(".post-hero__ribbon")).ToHaveTextAsync("Draft");
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = DraftTitle, Level = 1 })).ToBeVisibleAsync();
        await Expect(Page.Locator(".markdown-body")).ToBeVisibleAsync();
        await WaitForDarkPaletteAsync(Page);

        await AssertNoSeriousOrCriticalAxeViolationsAsync(Page);
    }

    /// <summary>
    /// Review Focus 2 (Task R3.6): at 390px neither Home nor a Post with a 300-char title and 20
    /// tags may scroll horizontally — document.documentElement.scrollWidth must fit the viewport.
    /// </summary>
    [Test]
    public async Task NoHorizontalOverflow_At390()
    {
        await EnsureFixturesAsync();
        await Page.SetViewportSizeAsync(390, 844);

        await Page.GotoAsync(BaseUrl);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.Locator(".post-card").First).ToBeVisibleAsync();
        var homeWidth = await Page.EvaluateAsync<int>("document.documentElement.scrollWidth");

        await Page.GotoAsync($"{BaseUrl}/post/{_longSlug}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await WaitForPostRenderedAsync(Page);
        var postWidth = await Page.EvaluateAsync<int>("document.documentElement.scrollWidth");

        Assert.That(homeWidth, Is.LessThanOrEqualTo(390),
            $"Home overflows horizontally at 390px (scrollWidth {homeWidth}px).");
        Assert.That(postWidth, Is.LessThanOrEqualTo(390),
            $"Post with a 300-char title and 20 tags overflows horizontally at 390px (scrollWidth {postWidth}px).");
    }

    /// <summary>
    /// The clamp's effect, not just its declaration: <c>-webkit-line-clamp</c> does nothing without the
    /// base rule's <c>display: -webkit-box</c> and <c>overflow: hidden</c>, so a stylesheet that lost
    /// either still declares the count (the computed display is not asserted: current Chromium reports a
    /// clamped <c>-webkit-box</c> as <c>flow-root</c>; the clamped height below is the outcome that matters). The summary is given far more text than the clamp lets
    /// through and must then be exactly <paramref name="lines"/> lines tall and cut off (its content
    /// taller than its box); one line's height is measured on the same element, so the check does not
    /// depend on how line-height is declared.
    /// </summary>
    private static async Task AssertSummaryClampsAsync(ILocator summary, int lines)
    {
        var json = await summary.EvaluateAsync<string>(
            """
            el => {
                const original = el.textContent;
                el.textContent = 'x';
                const oneLine = el.clientHeight;
                el.textContent = Array.from({ length: 300 }, (_, i) => 'clampword' + i).join(' ');
                const cs = getComputedStyle(el);
                const result = { oneLine, overflow: cs.overflow, clientHeight: el.clientHeight, scrollHeight: el.scrollHeight };
                el.textContent = original;
                return JSON.stringify(result);
            }
            """);
        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        var r = doc.RootElement;
        var oneLine = r.GetProperty("oneLine").GetDouble();
        Assert.That(r.GetProperty("overflow").GetString(), Is.EqualTo("hidden"), "the clamp needs overflow: hidden to cut the text off");
        Assert.That(r.GetProperty("clientHeight").GetDouble(), Is.EqualTo(oneLine * lines).Within(lines),
            $"a long summary shows exactly {lines} lines (one line is {oneLine:0.#}px): got {r.GetProperty("clientHeight").GetDouble():0.#}px");
        Assert.That(r.GetProperty("scrollHeight").GetDouble(), Is.GreaterThan(r.GetProperty("clientHeight").GetDouble()),
            "the rest of the text is cut off, not shown");
    }

    /// <summary>
    /// Ruling R3-6-2 (Task R3.2's deferred minor): a computed-style backstop for
    /// CardCssContractTests' CSS-text pins — the rendered cards must actually apply the featured
    /// media geometry and the summary line clamps, not just have them written in the stylesheet.
    /// </summary>
    [Test]
    public async Task CardCssContract_ComputedStyles_HoldOnRenderedCards()
    {
        await EnsureFixturesAsync();
        await Page.SetViewportSizeAsync(1440, 900);

        // Home always renders exactly one Featured card on page 1 (spec §5.2), whatever the shared
        // DB holds — its media geometry is pure CSS contract, independent of which post it is.
        await Page.GotoAsync(BaseUrl);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.Locator(".post-card--featured .post-card__media")).ToBeVisibleAsync();

        var featuredMedia = Page.Locator(".post-card--featured .post-card__media");
        await Expect(featuredMedia).ToHaveCSSAsync("aspect-ratio", "21 / 9");
        await Expect(featuredMedia).ToHaveCSSAsync("max-height", "420px");

        // The Featured summary clamps at 3 lines. The card only renders its summary when the post
        // has one (Review Focus 1's empty case renders no element); every post published through
        // the admin UI derives one from its body, and the runs are fully serial
        // (LevelOfParallelism(1)), so the featured card carries the clamped summary here.
        await Expect(Page.Locator(".post-card--featured .post-card__summary")).ToHaveCountAsync(1);
        await Expect(Page.Locator(".post-card--featured .post-card__summary"))
            .ToHaveCSSAsync("-webkit-line-clamp", "3");
        await AssertSummaryClampsAsync(Page.Locator(".post-card--featured .post-card__summary"), 3);

        // The List summary clamps at 2 lines — pinned where the fixture controls the content: the
        // search results for this run's id are exactly this class's fixture post, as List cards.
        await Page.GotoAsync($"{BaseUrl}/search?q={RunId}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.Locator(".post-card--list .post-card__summary")).ToHaveCountAsync(1);

        await Expect(Page.Locator(".post-card--list .post-card__summary"))
            .ToHaveCSSAsync("-webkit-line-clamp", "2");
        await AssertSummaryClampsAsync(Page.Locator(".post-card--list .post-card__summary"), 2);

        // --- List row geometry (fix round on Task R3.2's PostCard): mockup post-cards option D's
        // list rows are compact (~110-150px) with the banner CROPPED to the row (background-size:
        // cover in the mockup; spec §11: images are served as uploaded, the CSS crops with
        // object-fit). Three fixture posts with identical text and square/portrait/landscape
        // banners must render the same compact row shape, with the <img> cropped inside the media
        // box instead of driving the row height from its intrinsic aspect (measured before the fix:
        // banner rows 382px tall on an intrinsic-ratio <img>).
        await EnsureBannerFixturesAsync();
        await Page.GotoAsync($"{BaseUrl}/search?q={RowToken}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.Locator(".post-card--list")).ToHaveCountAsync(3);

        var rowHeights = new List<double>();
        foreach (var fixture in BannerFixtures)
        {
            var row = Page.Locator(".post-card--list").Filter(new LocatorFilterOptions { HasText = fixture.Suffix });
            await Expect(row).ToHaveCountAsync(1);
            await Expect(row).ToBeVisibleAsync();

            var img = row.Locator("img.banner-or-fallback__img");
            await Expect(img).ToHaveCSSAsync("object-fit", "cover");
            var imgHandle = await img.ElementHandleAsync();
            await Page.WaitForFunctionAsync("el => el.complete && el.naturalWidth > 0", imgHandle);
            var intrinsic = await img.EvaluateAsync<double[]>("el => [el.naturalWidth, el.naturalHeight]");
            Assert.That(intrinsic, Is.EqualTo(new double[] { fixture.Width, fixture.Height }),
                $"The '{fixture.Suffix}' fixture must render its own uploaded {fixture.Width}x{fixture.Height} banner " +
                "(the intrinsic aspect the row must not adopt).");

            var rowBox = await row.BoundingBoxAsync() ?? throw new InvalidOperationException("the list row has no layout box");
            var mediaBox = await row.Locator(".post-card__media").BoundingBoxAsync() ?? throw new InvalidOperationException("the list media has no layout box");
            var imgBox = await img.BoundingBoxAsync() ?? throw new InvalidOperationException("the banner img has no layout box");
            rowHeights.Add(rowBox.Height);

            // (a) the row stays compact whatever the banner's intrinsic aspect.
            Assert.That(rowBox.Height, Is.LessThanOrEqualTo(MaxCompactRowHeight),
                $"A list row with a {fixture.Width}x{fixture.Height} banner must stay compact (mockup rows are 110-150px) " +
                $"but rendered {rowBox.Height}px tall — the banner's intrinsic aspect is driving the row.");

            // (b) the banner renders cropped within the media box: the rendered box is not the
            // intrinsic aspect and it fits inside the row.
            Assert.That(imgBox.Height, Is.LessThanOrEqualTo(rowBox.Height + 1),
                $"The banner img must fit inside the row (img {imgBox.Height}px > row {rowBox.Height}px).");
            Assert.That(imgBox.Width, Is.LessThanOrEqualTo(mediaBox.Width + 1),
                $"The banner img must fit the media box width (img {imgBox.Width}px > media {mediaBox.Width}px).");
            Assert.That(imgBox.Height, Is.LessThanOrEqualTo(mediaBox.Height + 1),
                $"The banner img must fit the media box height (img {imgBox.Height}px > media {mediaBox.Height}px).");
            var renderedAspect = imgBox.Width / imgBox.Height;
            var intrinsicAspect = (double)fixture.Width / fixture.Height;
            Assert.That(Math.Abs(renderedAspect - intrinsicAspect), Is.GreaterThan(0.25),
                $"The banner must render cropped (rendered aspect {renderedAspect:0.##}), not at its intrinsic " +
                $"{fixture.Width}x{fixture.Height} aspect {intrinsicAspect:0.##}.");
        }

        // The row shape must not depend on the banner's aspect at all.
        Assert.That(rowHeights[1], Is.EqualTo(rowHeights[0]).Within(2),
            $"The portrait-banner row ({rowHeights[1]:0.#}px) must match the square-banner row ({rowHeights[0]:0.#}px).");
        Assert.That(rowHeights[2], Is.EqualTo(rowHeights[0]).Within(2),
            $"The landscape-banner row ({rowHeights[2]:0.#}px) must match the square-banner row ({rowHeights[0]:0.#}px).");
    }

    /// <summary>
    /// The post page's final rendered state (R3.3's flake guidance): highlight.js marks the code
    /// block after the interactive render, so the run sees the DOM the user sees.
    /// </summary>
    private async Task WaitForPostRenderedAsync(IPage page)
    {
        await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Level = 1 })).ToBeVisibleAsync();
        await Expect(page.Locator("pre code.hljs")).ToHaveCountAsync(1);
    }

    private static async Task AssertNoSeriousOrCriticalAxeViolationsAsync(IPage page)
    {
        AxeResult axe = await page.RunAxe();

        // Lower severities may be logged, not failed (task text).
        foreach (var violation in axe.Violations.Where(v => v.Impact is not ("serious" or "critical")))
        {
            TestContext.Progress.WriteLine(
                $"axe [not failing] {violation.Id} ({violation.Impact}): {violation.HelpUrl}");
        }

        var serious = axe.Violations.Where(v => v.Impact is "serious" or "critical").ToList();
        var report = string.Join("\n\n", serious.Select(v =>
            $"{v.Id} ({v.Impact}): {v.Help}\n{v.HelpUrl}\n" +
            string.Join("\n", v.Nodes.Select(n => $"  {n.Target} :: {n.Html}"))));

        Assert.That(serious, Is.Empty, $"serious/critical axe violations on {page.Url}:\n{report}");
    }

    private static string BuildLongTitle()
    {
        var title = new StringBuilder($"R36 {RunId} ");
        while (title.Length < 300)
        {
            title.Append("long title words ");
        }

        return title.ToString(0, 300);
    }

    /// <summary>One fixture post per run (unique via <see cref="RunId"/>), created through the admin UI.</summary>
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

            await LoginAsync("admin@example.com", "password");

            // Route/selector migration (rule 5-sanctioned): /admin → /admin/posts/new, the 20 tags
            // go through the chip input one by one, "Publish / Schedule" → "Publish now".
            await OpenNewPostEditorAsync();

            await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync(LongTitle);
            await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).BlurAsync();
            await Page.GetByLabel("Markdown").FillAsync(Body());
            await Page.GetByLabel("Markdown").BlurAsync();
            await AddTagsAsync(TagList.Split(", ", StringSplitOptions.RemoveEmptyEntries));

            await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Publish now" }).ClickAsync();
            await Page.WaitForURLAsync(
                url => url.StartsWith($"{BaseUrl}/admin/posts/") && url.EndsWith("/edit"),
                new PageWaitForURLOptions { Timeout = 15000 });
            _longSlug = await SlugForPostAsync(PostIdFromEditorUrl());

            await using var response = await Page.APIRequest.GetAsync($"{ApiUrl}/api/posts/{_longSlug}");
            Assert.That(response.Status, Is.EqualTo(200), $"The public API did not return the fixture post for slug {_longSlug}.");
            using var doc = JsonDocument.Parse(await response.TextAsync());
            Assert.That(doc.RootElement.GetProperty("tags").GetArrayLength(), Is.EqualTo(20),
                "The fixture post must carry all 20 tags (Review Focus 2).");

            await Page.GotoAsync($"{BaseUrl}/logout");
            _dataReady = true;
        }
        finally
        {
            DataGate.Release();
        }
    }

    /// <summary>
    /// The fixture body exercises the content renderings axe and overflow care about: headings,
    /// a link, a code block and an image with alt text and a title.
    /// </summary>
    private static string Body() =>
        $"Fixture intro {RunId} with enough words to start the page comfortably.\n\n" +
        "## First section\n\n" +
        $"First section body {RunId} with a [link to Home](/) inline.\n\n" +
        "## Second section\n\n" +
        "```csharp\n" +
        "var x = 1;\n" +
        "```\n\n" +
        "![A pattern](/branding/fallback-banner.svg \"Fallback banner\")\n";

    /// <summary>One run-keyed draft per run, created through the admin editor's Save draft (ruling
    /// R4-auth: fixtures go through the admin UI). Its slug comes from the Editor list's Review
    /// link — the public API cannot see drafts anonymously (EditorTests records why) — so both the
    /// id-routed post editor and the slug-routed review page address the same draft.</summary>
    private async Task EnsureDraftFixtureAsync()
    {
        if (_draftReady)
        {
            return;
        }

        await DataGate.WaitAsync();
        try
        {
            if (_draftReady)
            {
                return;
            }

            await LoginAsync("admin@example.com", "password");
            await OpenNewPostEditorAsync();
            await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync(DraftTitle);
            await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).BlurAsync();
            await Page.GetByLabel("Markdown").FillAsync(DraftBody());
            await Page.GetByLabel("Markdown").BlurAsync();
            await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save draft" }).ClickAsync();
            await Page.WaitForURLAsync(
                url => url.StartsWith($"{BaseUrl}/admin/posts/") && url.EndsWith("/edit"),
                new PageWaitForURLOptions { Timeout = 15000 });
            _draftId = PostIdFromEditorUrl();

            await Page.GotoAsync($"{BaseUrl}/logout");
            await LoginAsync("editor@example.com", "password");
            await Page.GotoAsync($"{BaseUrl}/editor");
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            var review = Page.Locator(".posts-grid__row", new PageLocatorOptions { HasText = DraftTitle })
                .GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = "Review" });
            await Expect(review).ToHaveAttributeAsync("href", new Regex("^/editor/posts/"));
            var href = await review.GetAttributeAsync("href");
            Assert.That(href, Does.StartWith("/editor/posts/"), "The Review link must lead to the draft review page.");
            _draftSlug = href!["/editor/posts/".Length..];

            await Page.GotoAsync($"{BaseUrl}/logout");
            _draftReady = true;
        }
        finally
        {
            DataGate.Release();
        }
    }

    /// <summary>The draft's body: prose, a heading and a link — the renderings the review surface
    /// shows (and the scans cover), on a post whose optional fields are all missing.</summary>
    private static string DraftBody() =>
        $"Review fixture {RunId} with enough words to read.\n\n" +
        "## Review section\n\n" +
        $"Review section body {RunId} with a [link to Home](/) inline.\n";

    /// <summary>Three banner fixture posts per run (unique via <see cref="RowToken"/>), created
    /// through the admin UI, each carrying this run's own uploaded PNG so the intrinsic banner
    /// aspect is exactly the fixture's by construction.</summary>
    private async Task EnsureBannerFixturesAsync()
    {
        if (_rowDataReady)
        {
            return;
        }

        await DataGate.WaitAsync();
        try
        {
            if (_rowDataReady)
            {
                return;
            }

            await LoginAsync("admin@example.com", "password");

            foreach (var (suffix, width, height) in BannerFixtures)
            {
                var fileName = $"{RowToken}-{width}x{height}.png";

                await OpenNewPostEditorAsync();

                await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync($"{RowToken} {suffix}");
                await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).BlurAsync();
                await Page.GetByLabel("Markdown").FillAsync(RowBody());
                await Page.GetByLabel("Markdown").BlurAsync();

                // The banner is the run's own uploaded image: a PNG clipped from this page at
                // exactly width×height (1:1 device scale), so the intrinsic aspect is the fixture's.
                var png = await Page.ScreenshotAsync(new PageScreenshotOptions
                {
                    Type = ScreenshotType.Png,
                    Clip = new Clip { X = 0, Y = 0, Width = width, Height = height }
                });
                await Page.GetByLabel("Upload banner").SetInputFilesAsync(new FilePayload
                {
                    Name = fileName,
                    MimeType = "image/png",
                    Buffer = png
                });
                await Expect(Page.GetByText(fileName).First).ToBeVisibleAsync();

                await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Publish now" }).ClickAsync();
                await Page.WaitForURLAsync(
                    url => url.StartsWith($"{BaseUrl}/admin/posts/") && url.EndsWith("/edit"),
                    new PageWaitForURLOptions { Timeout = 15000 });
            }

            await Page.GotoAsync($"{BaseUrl}/logout");
            _rowDataReady = true;
        }
        finally
        {
            DataGate.Release();
        }
    }

    /// <summary>Identical body for all three row fixtures — every list row's text side is identical,
    /// so any height difference between the rows comes from the banner alone.</summary>
    private static string RowBody() =>
        $"Banner row fixture {RowToken} with identical words in every fixture so the text side of each list row is the same.\n";
}
