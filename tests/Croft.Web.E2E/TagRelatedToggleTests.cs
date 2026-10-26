using System.Text.Json;

namespace Croft.Web.E2E;

/// <summary>
/// Brief 07k: a tag can stay out of "Related posts". The owner's flow end to end: two posts share
/// only a new tag, a third shares the first post's category; switching the tag's "Counts towards
/// related posts" off on the tags page makes the tag neutral — the related list shows the third
/// post first and the second right after it, ranked exactly like a post sharing nothing — while
/// the tag pill stays on the post and the tags table still fits a 390px page.
/// The fixture is its own: a run-keyed tag, category and three posts, so no other fixture's rows
/// can enter the assertions (the topic out-ranks every zero-scoring filler, and the sibling is
/// the newest zero-scoring candidate: this fixture runs alone on its worker's instance, so
/// nothing newer can fill ahead of it).
/// Break-it-once (recorded in the commit): removing the flag condition from the ranking fails
/// this test — the sibling then ranks above the topic post.
/// </summary>
public class TagRelatedToggleTests : WebTest
{
    [Test]
    public async Task SwitchingATagOutOfRelated_HidesTagOnlySiblings_ButKeepsTheTagPill()
    {
        await LoginAsync("admin@example.com", "password");
        var runId = Guid.NewGuid().ToString("N")[..12];
        var tagName = $"r07ktag{runId}";
        var categoryName = $"R07K Cat {runId}";

        await Page.GotoAsync($"{BaseUrl}/admin/tags");
        await WaitForInteractiveAsync(Page);
        await CreateCategoryAsync(categoryName);

        // Main and Sibling share only the new tag; Topic shares only Main's category.
        var mainSlug = await PublishPostAsync($"R07K Main {runId}", $"Main fixture {runId}.", tagName, categoryName);
        var siblingSlug = await PublishPostAsync($"R07K Sibling {runId}", $"Sibling fixture {runId}.", tagName, null);
        var topicSlug = await PublishPostAsync($"R07K Topic {runId}", $"Topic fixture {runId}.", null, categoryName);

        await Page.GotoAsync($"{BaseUrl}/admin/tags");
        await WaitForInteractiveAsync(Page);
        await FilterTagsAsync(tagName);
        var toggle = Page.GetByRole(AriaRole.Switch, new PageGetByRoleOptions
        {
            Name = $"Counts towards related posts: {tagName}",
        });
        await Expect(toggle).ToBeVisibleAsync();
        Assert.That(await toggle.GetAttributeAsync("aria-checked"), Is.EqualTo("true"),
            "a new tag counts towards related posts, so the switch starts on");

        await toggle.ClickAsync();

        // The row shows the save result like a rename does: the switch's state comes back from
        // the API through the circuit (the native click alone never touches this attribute).
        await Expect(toggle).ToHaveAttributeAsync("aria-checked", "false",
            new LocatorAssertionsToHaveAttributeOptions { Timeout = 15000 });
        var tagSlug = await WaitForPersistedAsync(tagName, countsTowardsRelated: false);

        // The admin table must not overflow the page at 390px wide (the existing admin-table
        // rule: the table is width 100% and its cells stay inside the page).
        await Page.SetViewportSizeAsync(390, 844);
        var scrollWidth = await Page.EvaluateAsync<int>("document.documentElement.scrollWidth");
        Assert.That(scrollWidth, Is.LessThanOrEqualTo(390),
            $"the tags table must not overflow the page at 390px wide (scrollWidth {scrollWidth}px)");
        await Page.SetViewportSizeAsync(1280, 720);

        await LogoutAsync();
        await Page.GotoAsync($"{BaseUrl}/post/{mainSlug}");
        await Expect(Page.Locator(".related-posts")).ToBeVisibleAsync();
        // A flag-off tag is neutral: the topic post (shared category) ranks first, and the
        // sibling (flag-off tag only) is listed where a post sharing nothing lands — right after
        // it, not above it (the tag counting) and not missing or sunken (a demotion).
        var hrefs = await Page.Locator(".related-posts__link").EvaluateAllAsync<string[]>(
            "els => els.map(el => el.getAttribute('href'))");
        Assert.That(hrefs, Is.Not.Empty, "the related list must render its cards");
        Assert.That(hrefs, Has.Length.GreaterThanOrEqualTo(2),
            "the topic and the sibling are both candidates and must both be listed");
        Assert.That(hrefs[0], Is.EqualTo($"/post/{topicSlug}"),
            "the third post (shared category) comes first in the related list");
        Assert.That(hrefs[1], Is.EqualTo($"/post/{siblingSlug}"),
            "the second post (flag-off tag only) follows, ranked like a post sharing nothing");
        // The tag itself is unchanged: its pill is still on the post.
        await Expect(Page.Locator($"a.tag-pill[href='/tag/{tagSlug}']")).ToBeVisibleAsync();
    }

    /// <summary>
    /// Waits on the concrete persisted outcome: the tags list read back from the API must carry
    /// the flag the owner just set. This is also the save-completion gate for the steps after it
    /// (Blazor Server saves leave no browser request to wait on). Returns the tag's slug.
    /// </summary>
    private async Task<string> WaitForPersistedAsync(string tagName, bool countsTowardsRelated)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        string? slug = null;
        bool? persisted = null;
        while (DateTime.UtcNow < deadline)
        {
            await using var response = await Page.APIRequest.GetAsync($"{ApiUrl}/api/tags?q={Uri.EscapeDataString(tagName)}");
            Assert.That(response.Status, Is.EqualTo(200), "the tags list must be readable through the API");
            using var doc = JsonDocument.Parse(await response.TextAsync());
            var matches = doc.RootElement.GetProperty("items").EnumerateArray()
                .Where(t => t.GetProperty("name").GetString() == tagName).ToList();
            Assert.That(matches, Has.Count.EqualTo(1),
                $"the tags list must hold exactly one tag named '{tagName}' (the name carries this run's id)");
            slug = matches[0].GetProperty("slug").GetString();
            persisted = matches[0].GetProperty("countsTowardsRelated").GetBoolean();
            if (persisted == countsTowardsRelated)
            {
                return slug!;
            }

            // Bounded poll of one concrete condition, spaced: back-to-back reads for 15 s load the
            // Postgres and host the other two instances' fixtures share.
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        Assert.Fail($"Tag '{tagName}' persisted CountsTowardsRelated={persisted}, expected {countsTowardsRelated}.");
        return string.Empty;
    }

    private async Task CreateCategoryAsync(string name)
    {
        await WaitForInteractiveAsync(Page);
        await Page.GetByLabel("New category").FillAsync(name);
        await Page.Locator(".mud-input-control", new PageLocatorOptions { Has = Page.GetByLabel("New category") })
            .Locator("button").First
            .ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = $"Icon for {name}" }))
            .ToBeVisibleAsync();
    }

    private async Task<string> PublishPostAsync(string title, string body, string? tagName, string? categoryName)
    {
        await OpenNewPostEditorAsync();

        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync(title);
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).BlurAsync();
        await Page.GetByLabel("Markdown").FillAsync(body);
        await Page.GetByLabel("Markdown").BlurAsync();
        if (tagName is not null)
        {
            await AddTagsAsync(tagName);
        }

        if (categoryName is not null)
        {
            await SelectCategoryAsync(categoryName);
        }

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Publish now" }).ClickAsync();
        await Page.WaitForURLAsync(
            url => url.StartsWith($"{BaseUrl}/admin/posts/") && url.EndsWith("/edit"),
            new PageWaitForURLOptions { Timeout = 30000 });

        return await SlugForPostAsync(PostIdFromEditorUrl());
    }

    private async Task SelectCategoryAsync(string categoryName)
    {
        // Select through the picker's search: the unfiltered list is a bounded window (the first
        // page of name-ordered categories), so a freshly created category can sort outside it.
        var category = Page.GetByRole(AriaRole.Combobox, new PageGetByRoleOptions { Name = "Category" });
        await category.ClickAsync();
        await category.FillAsync(categoryName);
        await Page.GetByRole(AriaRole.Option, new PageGetByRoleOptions { Name = categoryName }).ClickAsync();
    }

    private async Task FilterTagsAsync(string name)
    {
        await Page.GetByRole(AriaRole.Textbox, new PageGetByRoleOptions { Name = "Search tags" }).FillAsync(name);
        await Page.GetByRole(AriaRole.Textbox, new PageGetByRoleOptions { Name = "Search tags" }).PressAsync("Enter");
    }

    private async Task LogoutAsync()
    {
        await SignOutAsync();
    }
}
