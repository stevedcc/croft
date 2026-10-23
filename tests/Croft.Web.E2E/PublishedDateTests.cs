using Croft.Shared.Contracts;

namespace Croft.Web.E2E;

/// <summary>
/// Issue #1: clicking Publish now on a published post leaves its date alone, and Set date puts a lost date back.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class PublishedDateTests : WebTest
{
    private static readonly DateTime OriginalDate = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime LaterPostDate = new(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);

    private async Task<(Guid Id, string Slug)> CreatePublishedAsync(HttpClient client, string token, string title, string tag, DateTime? publishedAt)
    {
        using var created = await MockIdpApi.PostJsonAsync(client, ApiUrl, "/api/posts",
            new CreatePostRequest(title, "Body of the post.", Tags: [tag]), token);
        var id = created.RootElement.GetProperty("id").GetGuid();
        var slug = created.RootElement.GetProperty("slug").GetString()!;
        using var published = await MockIdpApi.PostJsonAsync(client, ApiUrl, $"/api/posts/{id}/publish",
            new PublishRequest(publishedAt), token);
        return (id, slug);
    }

    private ILocator MetaLine => Page.Locator(".post-meta__when");

    [Test]
    public async Task PublishNow_OnAPublishedPost_KeepsItsDate_AndSetDateMovesIt()
    {
        var runId = Guid.NewGuid().ToString("N")[..12];
        var tag = $"pubdate-{runId}";
        using var client = MockIdpApi.CreateClient();
        var token = await MockIdpApi.GetAdminAccessTokenAsync(client);
        var (id, slug) = await CreatePublishedAsync(client, token, $"Date keeper {runId}", tag, null);
        using (await MockIdpApi.PostJsonAsync(client, ApiUrl, $"/api/posts/{id}/publish", new PublishRequest(OriginalDate), token)) { }
        var (_, laterSlug) = await CreatePublishedAsync(client, token, $"Later post {runId}", tag, LaterPostDate);

        await LoginAsync("admin@example.com", "password");
        await Page.GotoAsync($"{BaseUrl}/admin/posts/{id}/edit");
        await WaitForInteractiveAsync(Page);
        await Expect(Page.GetByLabel("Publish date (UTC)")).ToHaveValueAsync("2026-10-01");
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Publish now" }).ClickAsync();
        await Expect(Page.Locator(".post-actions__saved")).ToHaveTextAsync("Published", new LocatorAssertionsToHaveTextOptions { Timeout = 15000 });

        await Page.GotoAsync($"{BaseUrl}/post/{slug}");
        await Expect(MetaLine).ToContainTextAsync("1 October 2026");

        await Page.GotoAsync($"{BaseUrl}/admin/posts/{id}/edit");
        await WaitForInteractiveAsync(Page);
        var setDate = Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Set date" });
        await Expect(setDate).ToBeDisabledAsync();
        await Page.GetByLabel("Publish date (UTC)").ClickAsync();
        var picker = Page.Locator(".mud-picker-open, .mud-popover-open").First;
        await picker.Locator(".mud-picker-calendar-header button:first-child").ClickAsync();
        await Expect(picker.Locator(".mud-picker-calendar-header p.mud-typography-align-center")).ToContainTextAsync("September 2026");
        await picker.Locator(".mud-picker-calendar-day:has-text('15')").Last.ClickAsync();
        await Page.Locator(".mud-picker-open, .mud-popover-open").First.WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Detached, Timeout = 5000 });
        await Expect(Page.GetByLabel("Publish date (UTC)")).ToHaveValueAsync("2026-09-15");
        await Expect(Page.GetByLabel("Publish time (UTC)")).ToHaveValueAsync("08:00");
        await Expect(setDate).ToBeEnabledAsync();

        await setDate.ClickAsync();

        // The stored date is now the entered one, so the button goes back to disabled.
        await Expect(setDate).ToBeDisabledAsync();
        await Page.GotoAsync($"{BaseUrl}/post/{slug}");
        await Expect(MetaLine).ToContainTextAsync("15 September 2026");

        await Page.GotoAsync($"{BaseUrl}/tag/{tag}");
        var hrefs = await Page.Locator("a.post-card__link").EvaluateAllAsync<string[]>("els => els.map(e => e.getAttribute('href'))");
        Assert.That(hrefs, Is.EqualTo(new[] { $"/post/{laterSlug}", $"/post/{slug}" }),
            "the back-dated post (15 September) is listed after the post published on 20 September");
    }

    [Test]
    public async Task APostEditedOnALaterDay_ShowsTheLongLineOnItsPage_AndOnlyTheDateOnItsCard()
    {
        var runId = Guid.NewGuid().ToString("N")[..12];
        var tag = $"metaline-{runId}";
        using var client = MockIdpApi.CreateClient();
        var token = await MockIdpApi.GetAdminAccessTokenAsync(client);
        using var created = await MockIdpApi.PostJsonAsync(client, ApiUrl, "/api/posts",
            new CreatePostRequest($"Meta line {runId}", "Body of the post.", Tags: [tag]), token);
        var id = created.RootElement.GetProperty("id").GetGuid();
        var slug = created.RootElement.GetProperty("slug").GetString()!;
        // The edit is "now" (the API's clock); the back-dated publish puts the publication on an earlier day.
        using var published = await MockIdpApi.PostJsonAsync(client, ApiUrl, $"/api/posts/{id}/publish", new PublishRequest(OriginalDate), token);
        var editedAt = published.RootElement.GetProperty("contentEditedAt").GetDateTimeOffset().UtcDateTime;
        var editedText = editedAt.ToString("d MMMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("en-GB"));

        await Page.GotoAsync($"{BaseUrl}/post/{slug}");
        await Expect(MetaLine).ToHaveTextAsync($"Published on 1 October 2026, last updated on {editedText} · 1 min read");

        await Page.GotoAsync($"{BaseUrl}/tag/{tag}");
        var card = Page.Locator($"a.post-card__link[href='/post/{slug}']");
        await Expect(card.Locator(".post-meta__when")).ToHaveTextAsync("1 October 2026 · 1 min read");
    }
}
