using System.Text.Json;
using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;

namespace Croft.Web.E2E;

/// <summary>
/// Brief 12e item 4: the hashtags of a category on Tags &amp; categories, end to end. The owner adds two (one for Bluesky only), saves, reloads the page and
/// finds them again, and a rename afterwards leaves them alone. The fixture is its own run-keyed category.
/// Break-it-once (recorded in the commit): dropping Hashtags from the page's update request fails the reload assertion.
/// </summary>
public class TagHashtagsTests : WebTest
{
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];

    private async Task OpenFilteredAsync(string categoryName)
    {
        await Page.GotoAsync($"{BaseUrl}/admin/tags");
        await WaitForInteractiveAsync(Page);
        var search = Page.GetByRole(AriaRole.Textbox, new PageGetByRoleOptions { Name = "Search categories" });
        await search.FillAsync(categoryName);
        await search.PressAsync("Enter");
        await Expect(RowOf(categoryName)).ToBeVisibleAsync();
    }

    private ILocator RowOf(string categoryName) =>
        Page.Locator("tr", new PageLocatorOptions { Has = Page.Locator($"button[aria-label='Icon for {categoryName}']") });

    [Test]
    public async Task CategoryHashtags_AreSaved_ShownAgainAfterAReload_AndKeptByARename()
    {
        await LoginAsync("admin@example.com", "password");
        var name = $"E2E Hashtags {RunId}";
        await Page.GotoAsync($"{BaseUrl}/admin/tags");
        await WaitForInteractiveAsync(Page);
        await Page.GetByLabel("New category").FillAsync(name);
        await Page.Locator(".mud-input-control", new PageLocatorOptions { Has = Page.GetByLabel("New category") }).Locator("button").First.ClickAsync();
        await Expect(RowOf(name)).ToBeVisibleAsync();

        var row = RowOf(name);
        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = $"Edit hashtags for {name}" }).ClickAsync();
        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Add hashtag" }).ClickAsync();
        await row.GetByLabel("Hashtag", new LocatorGetByLabelOptions { Exact = true }).Nth(0).FillAsync("#Homelab");
        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Add hashtag" }).ClickAsync();
        await row.GetByLabel("Hashtag", new LocatorGetByLabelOptions { Exact = true }).Nth(1).FillAsync("SelfHosted");
        await row.GetByLabel("Networks").Nth(1).SelectOptionAsync(new SelectOptionValue { Label = "Bluesky only" });

        // A rule-breaking row is explained under it and holds Save back.
        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Add hashtag" }).ClickAsync();
        await row.GetByLabel("Hashtag", new LocatorGetByLabelOptions { Exact = true }).Nth(2).FillAsync("two words");
        await Expect(row.Locator(".hashtag-editor__problem")).ToContainTextAsync("two words");
        await Expect(row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Save", Exact = true })).ToBeDisabledAsync();

        // The open editor with its error showing: no serious or critical accessibility violation.
        AxeResult axe = await Page.RunAxe(new AxeRunContext { Include = [new AxeSelector(".hashtag-cell")] });
        var serious = axe.Violations.Where(v => v.Impact is "serious" or "critical").ToList();
        Assert.That(serious, Is.Empty, string.Join("\n", serious.Select(v => $"{v.Id}: {v.Help}")));

        // The open editor still fits a phone-width page.
        await Page.SetViewportSizeAsync(390, 844);
        Assert.That(await Page.EvaluateAsync<int>("document.documentElement.scrollWidth"), Is.LessThanOrEqualTo(390), "the open editor must not widen the page at 390px");
        await Page.SetViewportSizeAsync(1280, 720);
        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Remove #two words" }).ClickAsync();

        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Save", Exact = true }).ClickAsync();
        await Expect(row.Locator(".hashtag-cell__text")).ToHaveTextAsync("#Homelab #SelfHosted");

        // Outcome: what the API stored, and what the page shows again after a full reload.
        await OpenFilteredAsync(name);
        await Expect(RowOf(name).Locator(".hashtag-cell__text")).ToHaveTextAsync("#Homelab #SelfHosted");
        Assert.That(await StoredHashtagsAsync(name), Is.EqualTo([("Homelab", 0), ("SelfHosted", 2)]), "Both = 0, Bluesky only = 2");
        await RowOf(name).GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = $"Edit hashtags for {name}" }).ClickAsync();
        await Expect(RowOf(name).GetByLabel("Networks").Nth(0)).ToHaveValueAsync("Both");
        await Expect(RowOf(name).GetByLabel("Networks").Nth(1)).ToHaveValueAsync("BlueskyOnly");

        // A rename sends no hashtags and must not wipe them.
        var renamed = $"E2E Hashtags Renamed {RunId}";
        await OpenFilteredAsync(name);
        await RowOf(name).GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = $"Rename {name}" }).ClickAsync();
        var field = RowOf(name).GetByRole(AriaRole.Textbox, new LocatorGetByRoleOptions { Name = "Name", Exact = true });
        await field.FillAsync(renamed);
        await field.PressAsync("Enter");
        await Expect(RowOf(renamed)).ToBeVisibleAsync();

        await OpenFilteredAsync(renamed);
        await Expect(RowOf(renamed).Locator(".hashtag-cell__text")).ToHaveTextAsync("#Homelab #SelfHosted");
        Assert.That(await StoredHashtagsAsync(renamed), Is.EqualTo([("Homelab", 0), ("SelfHosted", 2)]), "the rename kept the list");
    }

    private async Task<(string Name, int Networks)[]> StoredHashtagsAsync(string categoryName)
    {
        await using var listing = await Page.APIRequest.GetAsync($"{ApiUrl}/api/categories?q={Uri.EscapeDataString(categoryName)}&pageSize=10");
        Assert.That(listing.Status, Is.EqualTo(200), "the category list answers");
        using var json = JsonDocument.Parse(await listing.TextAsync());
        var stored = json.RootElement.GetProperty("items").EnumerateArray().Single(c => c.GetProperty("name").GetString() == categoryName);
        return [.. stored.GetProperty("hashtags").EnumerateArray().Select(h => (h.GetProperty("name").GetString()!, h.GetProperty("networks").GetInt32()))];
    }
}
