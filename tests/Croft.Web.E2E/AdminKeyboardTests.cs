using System.Text.Json;
using static Croft.Web.E2E.ColorContrast;

namespace Croft.Web.E2E;

/// <summary>
/// Brief 13g item 2 (review I-1, I-5): a keyboard user sees where focus is on the editor's two
/// transparent file inputs, and the Categories &amp; Tags rename keeps focus where the user is.
/// Outcomes are measured from the rendered page: the outline the browser computes on the element
/// that is really drawn, and which element holds focus after each key.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class AdminKeyboardTests : StaticReadingTest
{
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..10];

    private async Task<IPage> OpenAsync(string path, string theme)
    {
        var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new() { Width = 1440, Height = 900 },
            ColorScheme = theme == "dark" ? ColorScheme.Dark : ColorScheme.Light,
        });
        await context.AddInitScriptAsync($"localStorage.setItem('blog-theme', '{theme}')");
        var page = await context.NewPageAsync();
        await LoginAsync(page, "admin@example.com", "password");
        await page.GotoAsync($"{BaseUrl}{path}");
        await WaitForInteractiveAsync(page);
        // The circuit's first render replaces the prerendered DOM; a Tab before it lands on nodes that are then swapped.
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        return page;
    }

    [TestCase("Upload banner", "light")]
    [TestCase("Upload banner", "dark")]
    [TestCase("Upload inline image", "light")]
    [TestCase("Upload inline image", "dark")]
    public async Task FileInput_TabbedOnto_ShowsAnOutlineOnItsVisibleWrapper(string label, string theme)
    {
        var page = await OpenAsync("/admin/posts/new", theme);
        await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "New Post" })).ToBeVisibleAsync();

        // Real Tab presses, so the focus is keyboard focus (:focus-visible) as a user gets it. The input
        // itself is transparent, so an outline on it is invisible: what the user sees is the outline on
        // the wrapper that holds the input and its visible label. Matching and measuring happen in one
        // evaluation, so a re-render between the two cannot hand the measurement another element.
        string? json = null;
        for (var press = 0; press < 120 && json is null; press++)
        {
            await page.Keyboard.PressAsync("Tab");
            json = await page.EvaluateAsync<string?>(
                @"label => {
                    const input = document.activeElement;
                    if (!input || input.type !== 'file' || input.getAttribute('aria-label') !== label) return null;
                    const wrapper = input.parentElement;
                    const cs = getComputedStyle(wrapper);
                    const ancestors = [];
                    for (let p = wrapper.parentElement; p; p = p.parentElement) ancestors.push(getComputedStyle(p).backgroundColor);
                    return JSON.stringify({ style: cs.outlineStyle, width: parseFloat(cs.outlineWidth), color: cs.outlineColor, ancestors,
                        inputOpacity: getComputedStyle(input).opacity, wrapperClass: wrapper.className });
                }", label);
        }

        Assert.That(json, Is.Not.Null, $"Tab reaches the '{label}' file input");
        using var doc = JsonDocument.Parse(json!);
        var r = doc.RootElement;
        Assert.That(r.GetProperty("style").GetString(), Is.Not.EqualTo("none"),
            $"the '{label}' input's wrapper ({r.GetProperty("wrapperClass").GetString()}) draws no outline while the input has keyboard focus ({theme})");
        Assert.That(r.GetProperty("width").GetDouble(), Is.GreaterThanOrEqualTo(2), "outline width in px");
        var canvas = theme == "dark" ? new Rgba(0, 0, 0, 1) : new Rgba(255, 255, 255, 1);
        var backdrop = BackdropOf([.. r.GetProperty("ancestors").EnumerateArray().Select(e => e.GetString()!)], canvas);
        var outline = Composite(Parse(r.GetProperty("color").GetString()!), backdrop);
        Assert.That(Ratio(outline, backdrop), Is.GreaterThanOrEqualTo(3),
            $"the outline {r.GetProperty("color").GetString()} on the wrapper's backdrop contrasts at least 3:1 ({theme}), WCAG 1.4.11");
    }

    /// <summary>The names in the public lists after a rename. The categories and tags endpoints are public reads.</summary>
    private async Task<IReadOnlyList<string>> StoredNamesAsync(string kind, string q)
    {
        await using var listing = await Page.APIRequest.GetAsync($"{ApiUrl}/api/{kind}?q={Uri.EscapeDataString(q)}&pageSize=50");
        Assert.That(listing.Status, Is.EqualTo(200), $"the public {kind} list answers");
        using var json = JsonDocument.Parse(await listing.TextAsync());
        return [.. json.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!)];
    }

    [Test]
    public async Task FocusIn_WhenTheTargetNeverAppears_GivesUpWithAConsoleWarning()
    {
        // Brief 13i item 6: the retry loop (W-094) ends silently if its target never renders; a developer
        // looking at a lost focus must find the reason in the console. Two frames, not the default sixty.
        var page = await OpenAsync("/admin/tags", "light");

        var warning = await page.RunAndWaitForConsoleMessageAsync(
            () => page.EvaluateAsync("() => window.blogEditor.focusIn('no-such-element', 'input', 2)"),
            new PageRunAndWaitForConsoleMessageOptions { Predicate = message => message.Type == "warning", Timeout = 5000 });

        Assert.That(warning.Text, Does.Contain("focusIn").And.Contain("no-such-element").And.Contain("input"),
            "the warning names what it was looking for");
    }

    [TestCase("categories")]
    [TestCase("tags")]
    public async Task Rename_FocusMovesIntoTheField_EnterSaves_EscapeCancelsAndReturnsFocusToRename(string kind)
    {
        var name = $"R13g Alpha {kind} {RunId}";
        var renamed = $"R13g Beta {kind} {RunId}";
        using var client = MockIdpApi.CreateClient();
        var token = await MockIdpApi.GetAdminAccessTokenAsync(client);
        await MockIdpApi.PostJsonAsync(client, ApiUrl, $"/api/{kind}", JsonSerializer.Serialize(name), token);

        var page = await OpenAsync("/admin/tags", "light");
        var search = page.GetByRole(AriaRole.Textbox, new PageGetByRoleOptions { Name = kind == "categories" ? "Search categories" : "Search tags" });
        await search.FillAsync(RunId);
        await search.PressAsync("Enter");

        // Activate Rename from the keyboard: the field replaces the button, so focus must follow.
        var rename = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = $"Rename {name}" });
        await rename.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        var field = page.GetByRole(AriaRole.Textbox, new PageGetByRoleOptions { Name = "Name", Exact = true });
        await WaitedFor("the rename field to take focus", () => Expect(field).ToBeFocusedAsync());

        // Enter commits: the API shows the new name, and focus is back on the (renamed) row's Rename button.
        await page.Keyboard.PressAsync("Control+A");
        await page.Keyboard.TypeAsync(renamed);
        await page.Keyboard.PressAsync("Enter");
        await WaitedFor("Enter to save the new name", () => Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = $"Rename {renamed}" })).ToBeFocusedAsync());
        Assert.That(await StoredNamesAsync(kind, RunId), Does.Contain(renamed).And.Not.Contain(name), "Enter saved the rename through the API");

        // Escape cancels: the old (now the renamed) name stays, nothing is saved, focus is on Rename.
        await page.Keyboard.PressAsync("Enter");
        await WaitedFor("the rename field to take focus again", () => Expect(field).ToBeFocusedAsync());
        await page.Keyboard.PressAsync("Control+A");
        await page.Keyboard.TypeAsync("discarded");
        await page.Keyboard.PressAsync("Escape");
        await WaitedFor("Escape to return focus to Rename", () => Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = $"Rename {renamed}" })).ToBeFocusedAsync());
        await Expect(field).ToHaveCountAsync(0);
        // The page's save runs on a circuit round trip: wait for the interface to settle before reading the API.
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        Assert.That(await StoredNamesAsync(kind, RunId), Does.Contain(renamed).And.Not.Contain("discarded"),
            "Escape saved nothing");
        Assert.That(await StoredNamesAsync(kind, "discarded"), Is.Empty, "no row carries the discarded text");
    }

    [Test]
    public async Task DisabledDelete_ExplainsItselfInVisibleText()
    {
        var categoryName = $"R13g InUse {RunId}";
        var category = await CreateCategoryAsync(categoryName);
        await PublishAsync($"R13g InUse Post {RunId}", "Body.", categoryId: category.Id);

        var page = await OpenAsync("/admin/tags", "light");
        var search = page.GetByRole(AriaRole.Textbox, new PageGetByRoleOptions { Name = "Search categories" });
        await search.FillAsync(categoryName);
        await search.PressAsync("Enter");
        var row = page.Locator("tr", new PageLocatorOptions { Has = page.Locator($"button[aria-label='Icon for {categoryName}']") });
        var delete = row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Delete", Exact = true });
        await Expect(delete).ToBeDisabledAsync();

        // No hover: the reason is on the page, and the button is described by it.
        var reasonId = await delete.GetAttributeAsync("aria-describedby");
        Assert.That(reasonId, Is.Not.Null.And.Not.Empty, "the disabled Delete points at its reason");
        var reason = page.Locator($"#{reasonId}");
        await Expect(reason).ToBeVisibleAsync();
        await Expect(reason).ToContainTextAsync("cannot be deleted");
    }
}
