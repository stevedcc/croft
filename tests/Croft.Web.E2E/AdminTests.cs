using System.Text.Json;
using System.Text.RegularExpressions;
using MudBlazor;

namespace Croft.Web.E2E;

/// <summary>
/// Admin-area E2E. The media library here; later admin tasks append their tests.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class AdminTests : WebTest
{
    private const string BrokenImageWarning = "Posts embedding the image in their content will show a broken image.";

    [Test]
    public async Task MediaLibrary_MultiUpload_RendersBothTiles_WithImagesLoaded()
    {
        await LoginAsync("admin@example.com", "password");
        var runId = Guid.NewGuid().ToString("N");
        var one = CreateImageFile($"{runId}-one.png");
        var two = CreateImageFile($"{runId}-two.png");

        await Page.GotoAsync($"{BaseUrl}/admin/media");
        // Wait for the Blazor circuit to attach before dispatching DOM events: during the
        // prerender → interactive handoff a change/click event is lost (W-011). data-interactive
        // is the signal that the handoff is done; NetworkIdle ignores the circuit's WebSocket.
        await WaitForInteractiveAsync(Page);
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Media library" })).ToBeVisibleAsync();

        await Page.GetByLabel("Upload images").SetInputFilesAsync([one, two]);

        var tileOne = Page.Locator(".media-tile", new PageLocatorOptions { HasText = Path.GetFileName(one) });
        var tileTwo = Page.Locator(".media-tile", new PageLocatorOptions { HasText = Path.GetFileName(two) });
        await Expect(tileOne).ToBeVisibleAsync();
        await Expect(tileTwo).ToBeVisibleAsync();

        // Rule 7 outcome: both tiles carry images that actually load, not just markup.
        await AssertImageLoadsAsync(tileOne.Locator("img"));
        await AssertImageLoadsAsync(tileTwo.Locator("img"));

        // Clean up the uploads through the grid itself — also the delete-success outcome.
        await DeleteTileAsync(tileOne);
        await DeleteTileAsync(tileTwo);
    }

    /// <summary>The alt text the API holds for the uploaded file, read as the admin.</summary>
    private async Task<string?> SavedAltTextAsync(string fileName)
    {
        using var client = MockIdpApi.CreateClient();
        var token = await MockIdpApi.GetAdminAccessTokenAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{ApiUrl}/api/media?page=1&pageSize=10&q={Uri.EscapeDataString(fileName)}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = document.RootElement.GetProperty("items").EnumerateArray().ToList();
        // A search that matches more than one asset is a broken premise (the file name carries a
        // fresh run id), reported as such: a Single() would throw an InvalidOperationException
        // from inside the caller's retry and say nothing about the media search.
        Assert.That(items, Has.Count.EqualTo(1),
            $"the media search for '{fileName}' must match exactly one asset, but matched {items.Count}: "
            + string.Join(", ", items.Select(i => i.TryGetProperty("originalName", out var n) ? n.GetString() : "?")));
        var item = items[0];
        return item.TryGetProperty("altText", out var alt) && alt.ValueKind == JsonValueKind.String ? alt.GetString() : null;
    }

    /// <summary>
    /// Waits, bounded and awaited, until the API holds <paramref name="expected"/> as the alt text of
    /// the uploaded file. The failure says what the API held at the end, and a search that does not
    /// match exactly one asset fails at once with that fact (see <see cref="SavedAltTextAsync"/>).
    /// </summary>
    private async Task WaitForSavedAltTextAsync(string fileName, string expected, TimeSpan bound)
    {
        var deadline = DateTime.UtcNow + bound;
        string? saved;
        while ((saved = await SavedAltTextAsync(fileName)) != expected)
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail($"the API still holds alt text '{saved ?? "(none)"}' for '{fileName}' {bound.TotalSeconds:0} s after the blur; expected '{expected}'");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
    }

    [Test]
    public async Task MediaLibrary_AltText_SavesOnBlur_AndCopyMarkdownYieldsTheExactMarkdown()
    {
        await Page.Context.GrantPermissionsAsync(["clipboard-read", "clipboard-write"]);
        await LoginAsync("admin@example.com", "password");
        var runId = Guid.NewGuid().ToString("N");
        var path = CreateImageFile($"{runId}-alt.png");
        var name = Path.GetFileName(path);

        await Page.GotoAsync($"{BaseUrl}/admin/media");
        await WaitForInteractiveAsync(Page);
        await Page.GetByLabel("Upload images").SetInputFilesAsync(path);

        var tile = Page.Locator(".media-tile", new PageLocatorOptions { HasText = name });
        await Expect(tile).ToBeVisibleAsync();

        const string alt = "Sunset over the hills";
        await tile.GetByLabel("Alt text").FillAsync(alt);
        await tile.GetByLabel("Alt text").BlurAsync();

        // The blur starts the save on the server; the reload below must come after the API has
        // it, or the page being loaded can read the old value (brief 13e item 2: the circuit no
        // longer fetches the library a second time, which had masked that race).
        await WaitForSavedAltTextAsync(name, alt, TimeSpan.FromSeconds(10));

        // The save must reach the API: after a reload the field still holds the value.
        await Page.ReloadAsync();
        await WaitForInteractiveAsync(Page);
        tile = Page.Locator(".media-tile", new PageLocatorOptions { HasText = name });
        await Expect(tile.GetByLabel("Alt text")).ToHaveValueAsync(alt);

        var src = await tile.Locator("img").GetAttributeAsync("src");
        Assert.That(src, Does.Match("^/api/media/[0-9a-fA-F-]{36}$"),
            "Copy Markdown must use the DTO's relative /api/media/{id} URL.");
        var expectedMarkdown = $"![{alt}]({src})";

        await tile.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Copy Markdown" }).ClickAsync();
        await ClipboardReadEqualsAsync(expectedMarkdown);
        Assert.That(await Page.EvaluateAsync<string>("navigator.clipboard.readText()"), Is.EqualTo(expectedMarkdown),
            "Copy Markdown must yield exactly ![alt](/api/media/{id}).");

        await tile.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Copy URL" }).ClickAsync();
        await ClipboardReadEqualsAsync(src);
        Assert.That(await Page.EvaluateAsync<string>("navigator.clipboard.readText()"), Is.EqualTo(src),
            "Copy URL must yield the asset's URL.");

        await DeleteTileAsync(tile);
    }

    [Test]
    public async Task MediaLibrary_DeleteBannerImage_IsRefused_WithThePostTitleInTheMessage()
    {
        await LoginAsync("admin@example.com", "password");

        // Banner fixture through the admin editor (ruling R4-auth): on a new post the banner upload
        // stores a global image, which the media library lists and must refuse to delete.
        var title = $"R43 Banner {Guid.NewGuid():N}";
        await OpenNewPostEditorAsync();
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync(title);
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).BlurAsync();
        await Page.GetByLabel("Markdown").FillAsync("Banner fixture for the delete-refusal check.");
        await Page.GetByLabel("Markdown").BlurAsync();
        await Page.GetByLabel("Upload banner").SetInputFilesAsync(Path.Combine(AppContext.BaseDirectory, "test-image.png"));
        await Expect(Page.GetByText("test-image.png").First).ToBeVisibleAsync();
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Publish now" }).ClickAsync();
        await ExpectEditorUrlAsync();
        var slug = await SlugForPostAsync(PostIdFromEditorUrl());

        string bannerUrl;
        await using (var response = await Page.APIRequest.GetAsync($"{ApiUrl}/api/posts/{slug}"))
        {
            Assert.That(response.Status, Is.EqualTo(200), $"The public API did not return the post for slug {slug}.");
            using var doc = JsonDocument.Parse(await response.TextAsync());
            bannerUrl = doc.RootElement.GetProperty("bannerImage").GetProperty("url").GetString()!;
        }

        await Page.GotoAsync($"{BaseUrl}/admin/media");
        await WaitForInteractiveAsync(Page);
        var tile = Page.Locator($".media-tile:has(img[src='{bannerUrl}'])");
        await Expect(tile).ToBeVisibleAsync();

        await tile.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Delete", Exact = true }).ClickAsync();
        await Expect(tile.GetByText(BrokenImageWarning)).ToBeVisibleAsync();
        await tile.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Delete image" }).ClickAsync();

        // Rule 7 outcome: the delete is refused and the message names the post.
        await Expect(Page.Locator(".media-grid__error")).ToContainTextAsync(title);
        await Expect(tile).ToBeVisibleAsync();
        await AssertImageLoadsAsync(tile.Locator("img"));
    }

    // WORKAROUND(W-071, Playwright): the clipboard is read after a circuit round trip, so the test waits on the outcome
    /// <summary>
    /// Waits for the clipboard to hold <paramref name="expected"/>. The copy runs through the
    /// Blazor circuit after the click, so reading the clipboard immediately can beat it; this
    /// waits on the observable outcome (and times out if the copy never lands).
    /// </summary>
    private async Task ClipboardReadEqualsAsync(string expected)
    {
        await Page.WaitForFunctionAsync(
            "async expected => (await navigator.clipboard.readText()) === expected",
            expected,
            new PageWaitForFunctionOptions { Timeout = 5000 });
    }
    // END WORKAROUND(W-071)

    [Test]
    public async Task TagsPage_IconPicker_SetsCategoryIcon_TheCategoryPageShowsIt()
    {
        await LoginAsync("admin@example.com", "password");
        var runId = Guid.NewGuid().ToString("N");
        var categoryName = $"R44 Icon {runId}";

        await Page.GotoAsync($"{BaseUrl}/admin/tags");
        await WaitForInteractiveAsync(Page);
        await CreateCategoryAsync(categoryName);

        // The test sets the icon on its own category only: it used to pick whichever category the
        // sidebar listed first, which is another fixture's (the sidebar shows the top 12 by
        // published-post count) and left that fixture's category with a changed icon. A category
        // needs a post to have a public page, so give it one.
        await PublishPostAsync($"R44 Icon Post {runId}", $"Icon picker fixture {runId}.", categoryName);

        await Page.GotoAsync($"{BaseUrl}/admin/tags");
        await WaitForInteractiveAsync(Page);
        await FilterCategoriesAsync(categoryName);
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = $"Icon for {categoryName}" }).ClickAsync();

        var dialog = Page.Locator(".mud-dialog");
        await Expect(dialog).ToBeVisibleAsync();
        await dialog.GetByLabel("Search icons").FillAsync("mount");
        await dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Terrain", Exact = true }).ClickAsync();
        await dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Save", Exact = true }).ClickAsync();
        await Expect(dialog).ToBeHiddenAsync();

        // The dialog closes before the page's save runs (IconPickerDialog.Save closes at
        // once and TagsPage awaits the result afterwards), so wait for the admin table's own
        // outcome — the row renders the chosen Terrain icon — before leaving the admin page.
        var rowIconSelector = $"button[aria-label='Icon for {categoryName}'] .category-icon";
        await Page.WaitForFunctionAsync(
            "([selector, expected]) => { const icon = document.querySelector(selector); return icon !== null && Array.from(icon.querySelectorAll('path')).map(p => p.getAttribute('d')).join('|') === expected; }",
            new object[] { rowIconSelector, IconPathData(Icons.Material.Filled.Terrain) },
            new PageWaitForFunctionOptions { Timeout = 15000 });

        // Rule 7 outcome: the icon is stored (the public API returns it for this category) and the
        // category's own public page renders the chosen Terrain icon, not just any icon.
        await using var listing = await Page.APIRequest.GetAsync(
            $"{ApiUrl}/api/categories?q={Uri.EscapeDataString(categoryName)}&pageSize=10");
        Assert.That(listing.Status, Is.EqualTo(200), "the public category list answers");
        using var json = System.Text.Json.JsonDocument.Parse(await listing.TextAsync());
        var stored = json.RootElement.GetProperty("items").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == categoryName);
        Assert.That(stored.GetProperty("icon").GetString(), Is.EqualTo("Terrain"), "the chosen icon was stored");

        await Page.GotoAsync($"{BaseUrl}/category/{stored.GetProperty("slug").GetString()}");
        var icon = Page.Locator(".page-header__title .category-icon");
        await Expect(icon).ToBeVisibleAsync();
        Assert.That(await IconPathDataAsync(icon), Is.EqualTo(IconPathData(Icons.Material.Filled.Terrain)),
            "the category's page must show the chosen Terrain icon");
    }

    [Test]
    public async Task TagsPage_CategoryDelete_IsDisabledWhilePostsUseIt_WithATooltip()
    {
        await LoginAsync("admin@example.com", "password");
        var runId = Guid.NewGuid().ToString("N");
        var categoryName = $"R44 InUse {runId}";

        await Page.GotoAsync($"{BaseUrl}/admin/tags");
        await WaitForInteractiveAsync(Page);
        await CreateCategoryAsync(categoryName);
        await PublishPostAsync($"R44 InUse Post {runId}", $"Delete-disabled fixture {runId}.", categoryName);

        await Page.GotoAsync($"{BaseUrl}/admin/tags");
        await WaitForInteractiveAsync(Page);
        await FilterCategoriesAsync(categoryName);
        var row = Page.Locator("tr", new PageLocatorOptions
        {
            Has = Page.Locator($"button[aria-label='Icon for {categoryName}']"),
        });
        await Expect(row).ToBeVisibleAsync();

        await Expect(row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Delete", Exact = true }))
            .ToBeDisabledAsync();

        // The tooltip says why. Hover the wrapper's padding: a disabled button swallows the
        // hover, so the pointer must land on the wrapper itself.
        await row.Locator(".admin-table__actions").HoverAsync(new LocatorHoverOptions
        {
            Position = new Microsoft.Playwright.Position { X = 1, Y = 1 },
        });
        await Expect(Page.GetByText(DeleteDisabledTooltip)).ToBeVisibleAsync();
    }

    [Test]
    public async Task PostAnalyticsPage_ShowsTotalViews_AndTheChartData()
    {
        var (slug, postId) = await CreatePublishedPostAsync();

        await using (var anonContext = await Browser.NewContextAsync())
        {
            var anonPage = await anonContext.NewPageAsync();
            await VisitPostCountingBeaconsAsync(anonPage, $"{BaseUrl}/post/{slug}", visits: 1);
        }

        await Page.GotoAsync($"{BaseUrl}/admin/posts/{postId}/analytics");
        await WaitForInteractiveAsync(Page);

        await Expect(Page.GetByText("Total views: 1", new PageGetByTextOptions { Exact = true })).ToBeVisibleAsync();

        // Rule 7 outcome: the chart draws the daily series as a real line, not just a container.
        var line = Page.Locator(".mud-chart-serie.mud-chart-line");
        await Expect(line).ToBeVisibleAsync();
        var d = await line.GetAttributeAsync("d");
        Assert.That(d, Is.Not.Null.And.Contains("L"), "the chart line must carry the daily series, e.g. 'M 30 275 L ...'");
    }

    private const string DeleteDisabledTooltip = "Cannot delete while posts use this category.";

    [Test]
    public async Task TagsPage_DeleteTooltip_OnlyOnTheDisabledDelete()
    {
        await LoginAsync("admin@example.com", "password");
        var runId = Guid.NewGuid().ToString("N");
        var usedName = $"R53 Used {runId}";
        var unusedName = $"R53 Unused {runId}";

        await Page.GotoAsync($"{BaseUrl}/admin/tags");
        await WaitForInteractiveAsync(Page);
        await CreateCategoryAsync(usedName);
        await CreateCategoryAsync(unusedName);
        await PublishPostAsync($"R53 Tooltip Post {runId}", "Tooltip fixture body.", usedName);

        await Page.GotoAsync($"{BaseUrl}/admin/tags");
        await WaitForInteractiveAsync(Page);
        // Filter to this run's two categories (they share the run id): the table pages its
        // name-ordered rows, so an unfiltered page may hold neither row.
        await FilterCategoriesAsync(runId);
        var usedRow = RowWithIconButton(usedName);
        var unusedRow = RowWithIconButton(unusedName);
        await Expect(usedRow.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Delete", Exact = true }))
            .ToBeDisabledAsync();
        await Expect(unusedRow.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Delete", Exact = true }))
            .ToBeEnabledAsync();

        // The tooltip explains why Delete is disabled (spec §6.5) — it shows on the disabled row…
        await usedRow.Locator(".admin-table__actions").HoverAsync(new LocatorHoverOptions
        {
            Position = new Microsoft.Playwright.Position { X = 1, Y = 1 },
        });
        await Expect(Page.GetByText(DeleteDisabledTooltip)).ToBeVisibleAsync();

        // …and a working Delete has nothing to explain, so it must not show there.
        await unusedRow.Locator(".admin-table__actions").HoverAsync(new LocatorHoverOptions
        {
            Position = new Microsoft.Playwright.Position { X = 1, Y = 1 },
        });
        await Expect(Page.GetByText(DeleteDisabledTooltip)).ToHaveCountAsync(0);
    }

    /// <summary>The categories-table row whose icon button identifies it.</summary>
    private ILocator RowWithIconButton(string categoryName) =>
        Page.Locator("tr", new PageLocatorOptions
        {
            Has = Page.Locator($"button[aria-label='Icon for {categoryName}']"),
        });

    /// <summary>Runs the categories table's server-side name filter (the tags page's own way to
    /// reach a row: the table pages name-ordered rows, so a row is located by filtering to it,
    /// never by assuming the current page holds it).</summary>
    private async Task FilterCategoriesAsync(string name)
    {
        var search = Page.GetByRole(AriaRole.Textbox, new PageGetByRoleOptions { Name = "Search categories" });
        await search.FillAsync(name);
        await search.PressAsync("Enter");
    }

    [Test]
    public async Task Editor_EditSubtitle_Save_ShowsItOnThePublicPost()
    {
        await LoginAsync("admin@example.com", "password");
        var title = $"R42 Subtitle {Guid.NewGuid():N}";
        await PublishPostAsync(title, "Subtitle fixture body.");
        var postId = PostIdFromEditorUrl();

        var subtitle = $"Written on the hillside {Guid.NewGuid():N}";
        await Page.GetByLabel("Subtitle").FillAsync(subtitle);
        await Page.GetByLabel("Subtitle").BlurAsync();
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save draft" }).ClickAsync();

        // WORKAROUND(W-065, Playwright): waitForFunction does not await async predicates, so the poll uses a synchronous XHR
        // The save travels over the Blazor circuit, so there is no browser response to await;
        // wait on the outcome instead: the served post page starts showing the new subtitle. The
        // poll is synchronous on purpose — Playwright's waitForFunction does not await async
        // predicates (it treats a pending promise as truthy).
        var slug = await SlugForPostAsync(postId);
        await Page.WaitForFunctionAsync(
            $@"slug => {{
                const request = new XMLHttpRequest();
                request.open('GET', '/post/' + slug, false);
                request.send();
                return request.responseText.includes('{subtitle}');
            }}",
            slug,
            new PageWaitForFunctionOptions { Timeout = 15000 });
        // END WORKAROUND(W-065)

        await Page.GotoAsync($"{BaseUrl}/post/{slug}");
        await Expect(Page.Locator(".post-hero__subtitle")).ToHaveTextAsync(subtitle);
    }

    [Test]
    public async Task Editor_Unpublish_RemovesThePostFromHome()
    {
        await LoginAsync("admin@example.com", "password");
        var title = $"R42 Unpublish {Guid.NewGuid():N}";
        await PublishPostAsync(title, "Unpublish fixture body.");
        var postId = PostIdFromEditorUrl();

        // Home is checked as a visitor: an admin's list includes drafts (the API's Admin view),
        // so only the logged-out view proves the public outcome.
        await SignOutAsync();
        await Page.GotoAsync(BaseUrl);
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = title })).ToBeVisibleAsync();

        await LoginAsync("admin@example.com", "password");
        await Page.GotoAsync($"{BaseUrl}/admin/posts/{postId}/edit");
        await WaitForInteractiveAsync(Page);
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Unpublish" }).ClickAsync();
        await Expect(Page.Locator(".status-badge")).ToHaveTextAsync("Draft");

        // Rule 7 outcome: the post is gone from Home, not merely marked draft in the editor.
        await SignOutAsync();
        await Page.GotoAsync(BaseUrl);
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = title })).ToHaveCountAsync(0);
    }

    [Test]
    public async Task Editor_Preview_UsesTheSamePostClassesAsThePublicPage()
    {
        await LoginAsync("admin@example.com", "password");
        var title = $"R42 Preview {Guid.NewGuid():N}";
        await PublishPostAsync(title, "Preview **fixture** body.");

        // The Preview tab renders with the real post surfaces (spec §7.1 rule 3): the hero and
        // body carry the same classes as the public page's.
        await Expect(Page.Locator(".post-edit__side .markdown-body strong")).ToHaveTextAsync("fixture");
        var previewHeroClass = await Page.Locator(".post-edit__side .post-hero").GetAttributeAsync("class");
        var previewBodyClass = await Page.Locator(".post-edit__side .markdown-body").GetAttributeAsync("class");
        Assert.That(previewHeroClass, Is.Not.Null.And.Not.Empty);
        Assert.That(previewBodyClass, Is.Not.Null.And.Not.Empty);

        var slug = await SlugForPostAsync(PostIdFromEditorUrl());
        await Page.GotoAsync($"{BaseUrl}/post/{slug}");
        await Expect(Page.Locator(".post-hero")).ToBeVisibleAsync();

        Assert.That(await Page.Locator(".post-hero").GetAttributeAsync("class"), Is.EqualTo(previewHeroClass),
            "the Preview hero must be the same PostHero component as the public page");
        Assert.That(await Page.Locator(".markdown-body").GetAttributeAsync("class"), Is.EqualTo(previewBodyClass),
            "the Preview body must be the same PostBody component as the public page");
    }

    [Test]
    public async Task Editor_InsertFromLibrary_InsertsTheMarkdownAtTheCursor()
    {
        await LoginAsync("admin@example.com", "password");
        var runId = Guid.NewGuid().ToString("N");
        var path = CreateImageFile($"{runId}-pick.png");
        var name = Path.GetFileName(path);
        const string alt = "Sunset over the hills";

        // Seed a global image through the media library, with alt text so the inserted markdown
        // is the grid's Copy Markdown shape: ![alt](/api/media/{id}).
        await Page.GotoAsync($"{BaseUrl}/admin/media");
        await WaitForInteractiveAsync(Page);
        await Page.GetByLabel("Upload images").SetInputFilesAsync(path);
        var tile = Page.Locator(".media-tile", new PageLocatorOptions { HasText = name });
        await Expect(tile).ToBeVisibleAsync();
        await tile.GetByLabel("Alt text").FillAsync(alt);
        await tile.GetByLabel("Alt text").BlurAsync();
        var src = await tile.Locator("img").GetAttributeAsync("src");
        Assert.That(src, Does.Match("^/api/media/[0-9a-fA-F-]{36}$"));

        // Caret mid-text, then pick the image from the library.
        await Page.GotoAsync($"{BaseUrl}/admin/posts/new");
        await WaitForInteractiveAsync(Page);
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync($"R42 Pick {runId}");
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).BlurAsync();
        var textarea = Page.GetByLabel("Markdown");
        await textarea.FillAsync("before after");
        await textarea.EvaluateAsync("el => { el.focus(); el.setSelectionRange(7, 7); }");

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Insert from library" }).ClickAsync();
        var dialog = Page.Locator(".mud-dialog");
        await Expect(dialog).ToBeVisibleAsync();
        await dialog.Locator($".media-tile:has-text('{name}') .media-tile__pick").ClickAsync();
        await Expect(dialog).ToBeHiddenAsync();

        // Rule 7 outcome: the markdown landed at the caret, not appended or dropped.
        await Expect(textarea).ToHaveValueAsync($"before ![{alt}]({src})after");
    }

    [Test]
    public async Task Editor_InsertFromLibrary_FreshPost_ShowsTheMediaGridEmptyState()
    {
        await LoginAsync("admin@example.com", "password");
        await Page.GotoAsync($"{BaseUrl}/admin/posts/new");
        await WaitForInteractiveAsync(Page);
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync($"R42 Empty {Guid.NewGuid():N}");
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).BlurAsync();
        await Page.GetByLabel("Markdown").FillAsync("Empty-state fixture.");
        await Page.GetByLabel("Markdown").BlurAsync();

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save draft" }).ClickAsync();
        await ExpectEditorUrlAsync();

        // Ruling R4-empty + spec §6.4: the picker opens on "All images" (the global library is
        // never empty in the shared dev DB), and a fresh post's own images are the deterministic
        // empty surface — so the scope switch is flipped before expecting it.
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Insert from library" }).ClickAsync();
        var dialog = Page.Locator(".mud-dialog");
        await Expect(dialog).ToBeVisibleAsync();
        await Expect(dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "All images" }))
            .ToHaveAttributeAsync("aria-pressed", "true");
        await dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "This post's images" }).ClickAsync();
        await Expect(dialog.Locator(".media-grid__empty")).ToHaveTextAsync("No images yet.");
    }

    [Test]
    public async Task PostsList_FilterByStatus_AndSearchByTitle()
    {
        await LoginAsync("admin@example.com", "password");
        var runId = Guid.NewGuid().ToString("N");
        var draftTitle = $"Nimbus {runId}";
        var publishedTitle = $"Zephyr {runId}";
        await CreateDraftAsync(draftTitle, "Draft filter fixture body.");
        await PublishPostAsync(publishedTitle, "Published filter fixture body.");

        // Bare /admin redirects to the posts list.
        await Page.GotoAsync($"{BaseUrl}/admin");
        await Page.WaitForURLAsync(
            url => url.EndsWith("/admin/posts"),
            new PageWaitForURLOptions { Timeout = 15000 });
        // The list prerenders: a chip clicked before the circuit's first render has no handler and the
        // click is lost, leaving the unfiltered list (issue #43).
        await WaitForInteractiveAsync(Page);

        // Rule 7 outcome: the filter changes the rows — the listed titles actually match it. The
        // status filters are also how each fixture reaches page 1: the shared dev DB holds posts
        // scheduled far in the future, which sort above everything on the unfiltered list.
        var draftChip = Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Draft", Exact = true });
        await draftChip.ClickAsync();
        await Expect(draftChip).ToHaveAttributeAsync("aria-pressed", "true");
        await Expect(RowFor(draftTitle)).ToBeVisibleAsync();
        await Expect(RowFor(publishedTitle)).ToHaveCountAsync(0);

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Published" }).ClickAsync();
        await Expect(RowFor(publishedTitle)).ToBeVisibleAsync();
        await Expect(RowFor(draftTitle)).ToHaveCountAsync(0);

        // And the title search narrows the rows to the matching title.
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "All" }).ClickAsync();
        await Page.GetByRole(AriaRole.Textbox, new PageGetByRoleOptions { Name = "Search titles" }).FillAsync($"Zephyr {runId}");
        await Page.GetByRole(AriaRole.Textbox, new PageGetByRoleOptions { Name = "Search titles" }).PressAsync("Enter");
        await Expect(RowFor(publishedTitle)).ToBeVisibleAsync();
        await Expect(RowFor(draftTitle)).ToHaveCountAsync(0);
    }

    [Test]
    public async Task PostsList_RowActions_NavigateToEditAnalyticsAndFeedbackTab()
    {
        await LoginAsync("admin@example.com", "password");
        var title = $"R41 Actions {Guid.NewGuid():N}";
        await PublishPostAsync(title, "Row actions fixture body.");
        var postId = PostIdFromEditorUrl();

        await SearchForRowAsync(title);
        var row = RowFor(title);
        await Expect(row).ToBeVisibleAsync();

        // Rule 7 outcome: each row action lands on the page that does the job.
        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Edit" }).ClickAsync();
        await ExpectUrlAsync($"{BaseUrl}/admin/posts/{postId}/edit");
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Edit Post" })).ToBeVisibleAsync();

        await SearchForRowAsync(title);
        row = RowFor(title);
        await Expect(row).ToBeVisibleAsync();
        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Analytics" }).ClickAsync();
        await ExpectUrlAsync($"{BaseUrl}/admin/posts/{postId}/analytics");
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = title })).ToBeVisibleAsync();
        await Expect(Page.Locator(".analytics__total")).ToContainTextAsync("Total views:");

        await SearchForRowAsync(title);
        row = RowFor(title);
        await Expect(row).ToBeVisibleAsync();
        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Feedback" }).ClickAsync();
        await ExpectUrlAsync($"{BaseUrl}/admin/posts/{postId}/edit?tab=feedback");
        // The Feedback tab is the one doing the job: its panel is what shows.
        await Expect(Page.GetByText("No feedback yet.")).ToBeVisibleAsync();
    }

    /// <summary>The posts-grid row showing this title.</summary>
    private ILocator RowFor(string title) =>
        Page.Locator(".posts-grid__row", new PageLocatorOptions { HasText = title });

    /// <summary>Opens the posts list and searches for the title so its row is on page 1. The
    /// shared dev DB accumulates posts scheduled far in the future, which sort above fresh rows
    /// on the unfiltered list (the same fixture rule as PostsList_FilterByStatus's filters).</summary>
    private async Task SearchForRowAsync(string title)
    {
        await Page.GotoAsync($"{BaseUrl}/admin/posts");
        await WaitForInteractiveAsync(Page);
        await Page.GetByRole(AriaRole.Textbox, new PageGetByRoleOptions { Name = "Search titles" }).FillAsync(title);
        await Page.GetByRole(AriaRole.Textbox, new PageGetByRoleOptions { Name = "Search titles" }).PressAsync("Enter");
    }

    // WORKAROUND(W-105, Playwright .NET): URL waits after in-circuit navigations are retrying assertions, not WaitForURLAsync
    /// <summary>Waits for the URL after a navigation inside the circuit (a <c>pushState</c>: Save draft,
    /// Publish now, a row action). The .NET client's <c>WaitForURLAsync</c> checks the frame's URL and
    /// then subscribes to the "navigated" event as two separate steps, so a navigation that lands
    /// between them is missed and the wait runs to its timeout while the URL is already right
    /// (reproduced: about 1 in 200 waits). <c>Expect(Page).ToHaveURLAsync</c> re-reads the URL until it matches.</summary>
    private Task ExpectUrlAsync(string url) =>
        Expect(Page).ToHaveURLAsync(url, new PageAssertionsToHaveURLOptions { Timeout = 15000 });

    /// <summary>The editor URL of a saved post, <c>/admin/posts/{id}/edit</c>.</summary>
    private Task ExpectEditorUrlAsync() =>
        Expect(Page).ToHaveURLAsync(
            new Regex($"^{Regex.Escape(BaseUrl)}/admin/posts/[^/]+/edit$"),
            new PageAssertionsToHaveURLOptions { Timeout = 15000 });
    // END WORKAROUND(W-105)

    /// <summary>Saves a post as a draft through the new editor.</summary>
    private async Task CreateDraftAsync(string title, string body)
    {
        await OpenNewPostEditorAsync();
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync(title);
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).BlurAsync();
        await Page.GetByLabel("Markdown").FillAsync(body);
        await Page.GetByLabel("Markdown").BlurAsync();

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save draft" }).ClickAsync();
        await ExpectEditorUrlAsync();
    }

    /// <summary>Creates a category through the new TagsPage UI (ruling R4-auth: fixtures go through the admin UI).</summary>
    private async Task CreateCategoryAsync(string name)
    {
        await WaitForInteractiveAsync(Page);
        await Page.GetByLabel("New category").FillAsync(name);
        await Page.Locator(".mud-input-control", new PageLocatorOptions { Has = Page.GetByLabel("New category") })
            .Locator("button").First
            .ClickAsync();
        // Wait on the concrete outcome: the category appears as a row of the categories table.
        await Expect(Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = $"Icon for {name}" }))
            .ToBeVisibleAsync();
    }

    private async Task PublishPostAsync(string title, string body, string? categoryName = null)
    {
        // Route/selector migration (rule 5-sanctioned): /admin → /admin/posts/new and
        // "Publish / Schedule" → "Publish now".
        await OpenNewPostEditorAsync();

        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync(title);
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).BlurAsync();
        await Page.GetByLabel("Markdown").FillAsync(body);
        await Page.GetByLabel("Markdown").BlurAsync();
        if (categoryName is not null)
        {
            // Select through the picker's search: the unfiltered list is a bounded window
            // (the first page of name-ordered categories), so a freshly created category can
            // sort outside it. Typing its unique name finds it wherever it sorts.
            var category = Page.GetByRole(AriaRole.Combobox, new PageGetByRoleOptions { Name = "Category" });
            await category.ClickAsync();
            await category.FillAsync(categoryName);
            await Page.GetByRole(AriaRole.Option, new PageGetByRoleOptions { Name = categoryName }).ClickAsync();
        }

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Publish now" }).ClickAsync();
        await ExpectEditorUrlAsync();
    }

    private async Task<string> CategorySlugAsync(string name)
    {
        await using var response = await Page.APIRequest.GetAsync($"{ApiUrl}/api/categories");
        Assert.That(response.Status, Is.EqualTo(200), "The API's category list is not reachable on :5000.");
        using var doc = JsonDocument.Parse(await response.TextAsync());
        foreach (var category in doc.RootElement.EnumerateArray())
        {
            if (category.GetProperty("name").GetString() == name)
            {
                return category.GetProperty("slug").GetString()!;
            }
        }

        Assert.Fail($"Category '{name}' was not created through the admin UI.");
        return string.Empty;
    }

    /// <summary>AnalyticsViewCountTests's fixture pattern: a post of its own, created via the admin UI.</summary>
    private async Task<(string Slug, Guid PostId)> CreatePublishedPostAsync()
    {
        await LoginAsync("admin@example.com", "password");
        var title = $"R44 Analytics {Guid.NewGuid():N}";
        await PublishPostAsync(title, "Analytics fixture body.");

        var postId = PostIdFromEditorUrl();
        return (await SlugForPostAsync(postId), postId);
    }

    /// <summary>AnalyticsViewCountTests's beacon helper pattern: every visit is counted once.</summary>
    private static async Task VisitPostCountingBeaconsAsync(IPage page, string url, int visits)
    {
        for (var visit = 0; visit < visits; visit++)
        {
            var beacon = page.WaitForResponseAsync(r =>
                r.Url.EndsWith("/api/analytics/pageview") && r.Request.Method == "POST");

            if (visit == 0)
                await page.GotoAsync(url);
            else
                await page.ReloadAsync();

            var response = await beacon;
            Assert.That(response.Status, Is.EqualTo(204), "View beacon was not accepted by the Web app.");
        }
    }

    /// <summary>The icon's SVG path data in render order, e.g. from a sidebar <c>.category-icon</c>.</summary>
    private static async Task<string> IconPathDataAsync(ILocator svg) =>
        await svg.EvaluateAsync<string>(
            "el => Array.from(el.querySelectorAll('path')).map(p => p.getAttribute('d')).join('|')");

    /// <summary>The same shape for a MudBlazor icon constant like <see cref="Icons.Material.Filled.Terrain"/>.</summary>
    private static string IconPathData(string materialIcon) =>
        string.Join("|", System.Text.RegularExpressions.Regex.Matches(materialIcon, "d=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value));

    /// <summary>Deletes a tile through the grid: Delete → confirm → gone.</summary>
    private async Task DeleteTileAsync(ILocator tile)
    {
        await tile.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Delete", Exact = true }).ClickAsync();
        await Expect(tile.GetByText(BrokenImageWarning)).ToBeVisibleAsync();
        await tile.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Delete image" }).ClickAsync();
        await Expect(tile).ToBeHiddenAsync();
    }

    /// <summary>A PNG of the shared test image under a name unique to this run.</summary>
    private static string CreateImageFile(string fileName)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "test-image.png");
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, fileName);
        File.Copy(source, path, overwrite: true);
        return path;
    }
}
