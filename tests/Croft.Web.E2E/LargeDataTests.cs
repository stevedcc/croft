using System.Diagnostics;

namespace Croft.Web.E2E;

/// <summary>
/// The list surfaces under the seeded large dataset (dev/seed-large-data.sh: 400 categories,
/// 3,000 tags, 3,000 published posts, 600 media items). Carries the "LargeData" NUnit category
/// and is excluded from the normal run (tests/Croft.Web.E2E.csproj's default filter); it runs
/// against a freshly seeded instance via dev/run-large-data-e2e.sh.
///
/// Timing measurement: a stopwatch from navigation start until the first interactive control has
/// responded (the control's concrete effect on the DOM), budget 2 s per surface. The waits inside
/// the window are the suite's condition waits — the interactivity hand-off and the control's
/// observable effect — never sleeps. Each measurement prints a "LARGE-DATA TIMING" line for the
/// run script's summary. The per-surface controls:
///   /admin/tags      the "Search tags" filter (server-side query narrowing 3,000 tags to one);
///   /admin/media     the "Search filenames" filter (narrowing 600 images to one);
///   /                the pager (page 2 must replace the listed posts);
///   /post/{slug}     a content control: a TOC link click must scroll its heading into view, and
///                    the post's own render outcome (the view beacon fires only once the post has
///                    rendered) and the hero heading are asserted in the same window — so the
///                    window measures the post content, not just the shell (the shell's theme
///                    switch response is checked too).
///
/// Fixtures use the seeded data or names unique to the run (RunId); the write checks assert
/// persistence by reloading and re-querying the server, not just the client-side row change.
/// </summary>
[Parallelizable(ParallelScope.None)]
[Category("LargeData")]
public class LargeDataTests : WebTest
{
    /// <summary>The seeded tag the tag-surface timing filters down to (unique match).</summary>
    private const string SeedTag = "Seed Tag 0001";

    /// <summary>The seeded image the media checks filter to (unique match).</summary>
    private const string SeedImage = "seed-image-0042.png";

    private static readonly TimeSpan InteractiveBudget = TimeSpan.FromSeconds(2);

    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];

    [Test]
    public async Task TagsPage_BecomesInteractiveWithinTwoSeconds()
    {
        await LoginAsync("admin@example.com", "password");
        await AssertInteractiveWithinBudgetAsync("/admin/tags", async () =>
        {
            await Page.GotoAsync($"{BaseUrl}/admin/tags");
            await WaitForInteractiveAsync(Page);

            await FilterTagsAsync(SeedTag);

            // The control responded: the server-side filter left exactly the one matching row.
            var rows = RowsNamed("Seed Tag");
            await Expect(rows).ToHaveCountAsync(1);
            await Expect(rows).ToContainTextAsync(SeedTag);
        });
    }

    [Test]
    public async Task MediaLibrary_BecomesInteractiveWithinTwoSeconds()
    {
        await LoginAsync("admin@example.com", "password");
        await AssertInteractiveWithinBudgetAsync("/admin/media", async () =>
        {
            await Page.GotoAsync($"{BaseUrl}/admin/media");
            await WaitForInteractiveAsync(Page);

            await FilterFilenamesAsync(SeedImage);

            // The control responded: one tile of the 600-image library is listed.
            var tiles = Page.Locator(".media-tile");
            await Expect(tiles).ToHaveCountAsync(1);
            await Expect(tiles).ToContainTextAsync(SeedImage);
        });
    }

    [Test]
    public async Task HomePage_BecomesInteractiveWithinTwoSeconds()
    {
        await AssertInteractiveWithinBudgetAsync("/", async () =>
        {
            await Page.GotoAsync(BaseUrl);
            await WaitForInteractiveAsync(Page);

            var pageOneTitle = (await Page.Locator(".post-card__title").First.TextContentAsync())?.Trim();
            Assert.That(pageOneTitle, Is.Not.Null.And.Not.Empty,
                "Home page 1 shows no post title to page away from.");

            // The pager is the list's own interactive control; page 2 must really replace the
            // listed posts (no featured card there, and a different post at the top).
            await Page.Locator("nav.page-pager")
                .GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = "2", Exact = true })
                .ClickAsync();
            await Page.WaitForURLAsync(url => url.Contains("page=2"), new PageWaitForURLOptions { Timeout = 10000 });
            await Expect(Page.Locator(".post-card--featured")).ToHaveCountAsync(0);
            await Expect(Page.Locator(".post-card__title").First).Not.ToContainTextAsync(pageOneTitle!);
        });
    }

    [Test]
    public async Task PostPage_BecomesInteractiveWithinTwoSeconds()
    {
        // The rail's TOC (the post's own control) shows at the wide breakpoints; below the
        // 1000px content threshold only the collapsed inline TOC exists (PostPageTests).
        await Page.SetViewportSizeAsync(1440, 900);
        var (slug, title) = await CreateHeadingPostAsync();

        await AssertInteractiveWithinBudgetAsync($"/post/{slug}", async () =>
        {
            // The app records the view from OnAfterRender only once the post is loaded: the
            // beacon's response is the interactive render of the post content having completed,
            // so the window covers the content render and not just the shell.
            var viewRecorded = Page.WaitForResponseAsync(
                r => r.Url.EndsWith("/api/analytics/pageview") && r.Request.Method == "POST",
                new PageWaitForResponseOptions { Timeout = 5000 });
            await Page.GotoAsync($"{BaseUrl}/post/{slug}");
            await WaitForInteractiveAsync(Page);

            // The post's own content must be rendered in the same window: a slow or missing
            // content render fails here even when the shell is already interactive.
            await Expect(Page.Locator(".post-hero__title")).ToHaveTextAsync(title);
            var beacon = await viewRecorded;
            Assert.That(beacon.Status, Is.EqualTo(204), "The view beacon was not accepted by the Web app.");

            // The shell's theme switch (kept as the shell control) — its label is re-rendered by
            // the Blazor circuit, so its response proves the page reached interactivity.
            var themeSwitch = Page.Locator("button.theme-switch");
            var before = (await themeSwitch.TextContentAsync())?.Trim();
            await themeSwitch.ClickAsync();
            await Expect(themeSwitch).Not.ToHaveTextAsync(before!);

            // The post's own interactive control: a TOC link click must scroll its heading into
            // view (PostPageTests' established outcome). The window ends on the post content
            // responding, not on the shell.
            await Page.Locator($".post-rail .toc a[href='/post/{slug}#second-section']").ClickAsync();
            await Page.WaitForURLAsync(
                url => url.StartsWith($"{BaseUrl}/post/{slug}#"),
                new PageWaitForURLOptions { Timeout = 10000 });
            await Page.WaitForFunctionAsync(
                "sel => { const el = document.querySelector(sel); if (!el) return false; const r = el.getBoundingClientRect(); return r.top < 200 && r.bottom > 0; }",
                "h2#second-section",
                new PageWaitForFunctionOptions { Timeout = 5000 });

            // The scroll-spy's marker is only set from OnAfterRender — the suite's existing
            // signal that the post content has rendered (PostPageTests). One more content
            // outcome in the same window.
            await Expect(Page.Locator(".post-rail .toc__link[aria-current='true']")).ToHaveCountAsync(1);
        });
    }

    [Test]
    public async Task TagsPage_CreateRenameDeleteATag_EachStepPersists()
    {
        await LoginAsync("admin@example.com", "password");
        await Page.GotoAsync($"{BaseUrl}/admin/tags");
        await WaitForInteractiveAsync(Page);

        var name = $"LD Tag {RunId}";
        var renamed = $"LD Renamed Tag {RunId}";

        // Create: the row appears (effect) and survives a reload (persisted).
        await Page.GetByLabel("New tag").FillAsync(name);
        await AddButtonFor("New tag").ClickAsync();
        await Expect(RowsNamed(name)).ToBeVisibleAsync();
        await ReloadAndFilterAsync(() => FilterTagsAsync(name));
        await Expect(RowsNamed(name)).ToBeVisibleAsync();

        // Rename: the row shows the new name (effect) and a reload shows it saved — and the old
        // name gone from the server.
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = $"Rename {name}" }).ClickAsync();
        await NameField().FillAsync(renamed);
        await NameField().BlurAsync();
        await Expect(RowsNamed(renamed)).ToBeVisibleAsync();
        await ReloadAndFilterAsync(() => FilterTagsAsync(renamed));
        await Expect(RowsNamed(renamed)).ToBeVisibleAsync();
        await FilterTagsAsync(name);
        await Expect(RowsNamed(name)).ToHaveCountAsync(0);
        await Expect(Page.GetByText("No tags yet.", new PageGetByTextOptions { Exact = true })).ToBeVisibleAsync();

        // Delete: the row is gone (effect) and stays gone after a reload (persisted).
        await FilterTagsAsync(renamed);
        await RowsNamed(renamed).GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Delete", Exact = true })
            .ClickAsync();
        await Expect(RowsNamed(renamed)).ToHaveCountAsync(0);
        await ReloadAndFilterAsync(() => FilterTagsAsync(renamed));
        await Expect(RowsNamed(renamed)).ToHaveCountAsync(0);
        await Expect(Page.GetByText("No tags yet.", new PageGetByTextOptions { Exact = true })).ToBeVisibleAsync();
    }

    [Test]
    public async Task TagsPage_CreateRenameDeleteACategory_EachStepPersists()
    {
        await LoginAsync("admin@example.com", "password");
        await Page.GotoAsync($"{BaseUrl}/admin/tags");
        await WaitForInteractiveAsync(Page);

        var name = $"LD Category {RunId}";
        var renamed = $"LD Renamed Category {RunId}";

        // Create: the row appears (effect) and survives a reload (persisted).
        await Page.GetByLabel("New category").FillAsync(name);
        await AddButtonFor("New category").ClickAsync();
        await Expect(RowsNamed(name)).ToBeVisibleAsync();
        await ReloadAndFilterAsync(() => FilterCategoriesAsync(name));
        await Expect(RowsNamed(name)).ToBeVisibleAsync();

        // Rename: the row shows the new name (effect), saved on the server (a reload finds it and
        // no longer the old name).
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = $"Rename {name}" }).ClickAsync();
        await NameField().FillAsync(renamed);
        await NameField().BlurAsync();
        await Expect(RowsNamed(renamed)).ToBeVisibleAsync();
        await ReloadAndFilterAsync(() => FilterCategoriesAsync(renamed));
        await Expect(RowsNamed(renamed)).ToBeVisibleAsync();
        await FilterCategoriesAsync(name);
        await Expect(RowsNamed(name)).ToHaveCountAsync(0);
        await Expect(Page.GetByText("No categories yet.", new PageGetByTextOptions { Exact = true })).ToBeVisibleAsync();

        // Delete: the row is gone (effect) and stays gone after a reload (persisted).
        await FilterCategoriesAsync(renamed);
        await RowsNamed(renamed).GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Delete", Exact = true })
            .ClickAsync();
        await Expect(RowsNamed(renamed)).ToHaveCountAsync(0);
        await ReloadAndFilterAsync(() => FilterCategoriesAsync(renamed));
        await Expect(RowsNamed(renamed)).ToHaveCountAsync(0);
        await Expect(Page.GetByText("No categories yet.", new PageGetByTextOptions { Exact = true })).ToBeVisibleAsync();
    }

    [Test]
    public async Task Editor_InsertFromLibrary_PicksAnImage_AndInsertsItsMarkdown()
    {
        await LoginAsync("admin@example.com", "password");
        await OpenNewPostEditorAsync();
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync($"LD Pick {RunId}");
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).BlurAsync();
        var textarea = Page.GetByLabel("Markdown");
        await textarea.FillAsync("before after");
        await textarea.EvaluateAsync("el => { el.focus(); el.setSelectionRange(7, 7); }");

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Insert from library" }).ClickAsync();
        var dialog = Page.Locator(".mud-dialog");
        await Expect(dialog).ToBeVisibleAsync();

        // With the full library loaded, the filename filter narrows the picker to the one image.
        await FilterFilenamesAsync(SeedImage, dialog);
        var tile = dialog.Locator(".media-tile", new LocatorLocatorOptions { HasText = SeedImage });
        await Expect(tile).ToBeVisibleAsync();
        var src = await tile.Locator("img").GetAttributeAsync("src");
        Assert.That(src, Does.Match("^/api/media/[0-9a-fA-F-]{36}$"),
            "The picker tile's image must use the asset's relative /api/media/{id} URL.");

        await tile.Locator(".media-tile__pick").ClickAsync();
        await Expect(dialog).ToBeHiddenAsync();

        // The outcome the editor is for: the picked image's markdown landed at the caret…
        await Expect(textarea).ToHaveValueAsync($"before ![]({src})after");

        // …and the URL is not merely well-shaped: the image actually resolves (a 200 image
        // response through the app's /api/media/{id} proxy).
        await using var image = await Page.APIRequest.GetAsync($"{BaseUrl}{src}");
        Assert.That(image.Status, Is.EqualTo(200), $"The picked image URL {src} did not resolve to the image.");
        image.Headers.TryGetValue("content-type", out var contentType);
        Assert.That(contentType, Does.StartWith("image/png"), $"The picked image URL {src} did not serve a PNG image.");
    }

    /// <summary>
    /// Times <paramref name="navigationAndInteraction"/> from navigation start until the first
    /// interactive control has responded, prints the measurement, and enforces the budget.
    /// </summary>
    private static async Task AssertInteractiveWithinBudgetAsync(string surface, Func<Task> navigationAndInteraction)
    {
        var stopwatch = Stopwatch.StartNew();
        await navigationAndInteraction();
        stopwatch.Stop();

        var ms = stopwatch.Elapsed.TotalMilliseconds;
        TestContext.Progress.WriteLine(
            $"LARGE-DATA TIMING {surface}: {ms:F0} ms (budget {InteractiveBudget.TotalMilliseconds:F0} ms)");
        Assert.That(stopwatch.Elapsed, Is.LessThan(InteractiveBudget),
            $"'{surface}' became interactive in {ms:F0} ms — over the {InteractiveBudget.TotalSeconds:F0} s budget.");
    }

    /// <summary>The admin-table rows whose text contains this unique fixture name.</summary>
    private ILocator RowsNamed(string name) =>
        Page.Locator("tr.admin-table__row", new PageLocatorOptions { HasText = name });

    /// <summary>The in-place name editor of the row being renamed. Exact: "Name" is a
    /// case-insensitive substring of every "Rename …" button's label.</summary>
    private ILocator NameField() => Page.GetByLabel("Name", new PageGetByLabelOptions { Exact = true });

    /// <summary>The "Add" adornment button of the given New tag / New category field.</summary>
    private ILocator AddButtonFor(string fieldLabel) =>
        Page.Locator(".mud-input-control", new PageLocatorOptions { Has = Page.GetByLabel(fieldLabel) })
            .Locator("button").First;

    /// <summary>Reloads the page and runs <paramref name="filterAsync"/> on the fresh DOM, so the
    /// assertions that follow read what the server holds, not what the client last rendered.</summary>
    private async Task ReloadAndFilterAsync(Func<Task> filterAsync)
    {
        await Page.ReloadAsync();
        await WaitForInteractiveAsync(Page);
        await filterAsync();
    }

    /// <summary>Runs the tags table's server-side name filter.</summary>
    private async Task FilterTagsAsync(string name)
    {
        var search = Page.GetByRole(AriaRole.Textbox, new PageGetByRoleOptions { Name = "Search tags" });
        await search.FillAsync(name);
        await search.PressAsync("Enter");
    }

    /// <summary>Runs the categories table's server-side name filter.</summary>
    private async Task FilterCategoriesAsync(string name)
    {
        var search = Page.GetByRole(AriaRole.Textbox, new PageGetByRoleOptions { Name = "Search categories" });
        await search.FillAsync(name);
        await search.PressAsync("Enter");
    }

    /// <summary>Runs the media grid's server-side filename filter (the page's grid, or the picker
    /// dialog's when <paramref name="scope"/> is given).</summary>
    private async Task FilterFilenamesAsync(string name, ILocator? scope = null)
    {
        var search = scope is null
            ? Page.GetByRole(AriaRole.Textbox, new PageGetByRoleOptions { Name = "Search filenames" })
            : scope.GetByRole(AriaRole.Textbox, new LocatorGetByRoleOptions { Name = "Search filenames" });

        // The precondition "with the full library loaded" made concrete: wait for the grid's
        // initial listing to render before typing. The filter is applied by an event (Enter), and
        // events dispatched while the dialog/page is still mounting its content can be silently
        // dropped (this suite's documented prerender → interactive race) — seen as a fill whose
        // text sat in the box while the unfiltered listing stayed and no filtered request was
        // ever sent. The first tile is the observable "the library is loaded" state.
        var firstTile = scope is null
            ? Page.Locator(".media-tile").First
            : scope.Locator(".media-tile").First;
        await Expect(firstTile).ToBeVisibleAsync();

        await search.FillAsync(name);
        await search.PressAsync("Enter");
    }

    /// <summary>A published post of its own with two headings (so the post page renders its TOC),
    /// created through the admin editor like the other suites' fixtures.</summary>
    private async Task<(string Slug, string Title)> CreateHeadingPostAsync()
    {
        await LoginAsync("admin@example.com", "password");
        var title = $"LD Post {RunId}";
        await OpenNewPostEditorAsync();
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync(title);
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).BlurAsync();
        // Enough text below the second heading that it can scroll to the top band of the
        // viewport (PostPageTests' fixture rule) — the scroll outcome is only observable on a
        // page tall enough to scroll.
        await Page.GetByLabel("Markdown").FillAsync(
            $"Large-data post-page fixture {RunId} with enough words to start the page comfortably.\n\n" +
            "## First section\n\n" +
            $"First section body {RunId} one. " + Filler(4) + "\n\n" +
            "## Second section\n\n" +
            $"Second section body {RunId} one.\n\n" + Filler(10));
        await Page.GetByLabel("Markdown").BlurAsync();
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Publish now" }).ClickAsync();
        await Page.WaitForURLAsync(
            url => url.StartsWith($"{BaseUrl}/admin/posts/") && url.EndsWith("/edit"),
            new PageWaitForURLOptions { Timeout = 15000 });
        return (await SlugForPostAsync(PostIdFromEditorUrl()), title);
    }

    /// <summary>Filler paragraphs (PostPageTests' fixture shape) to make the post page scrollable.</summary>
    private static string Filler(int paragraphs) =>
        string.Join("\n\n", Enumerable.Range(1, paragraphs).Select(n =>
            $"Paragraph {n}: when I opened the solution again after three years, the first thing " +
            "I noticed was how much state lived in places it had no business being, and nothing " +
            "told anything else that it had happened."));
}
