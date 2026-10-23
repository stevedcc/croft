using System.Text.Json;

namespace Croft.Web.E2E;

/// <summary>
/// Navigating between reading pages by link click shows the right content and is an enhanced
/// navigation (no full document load). The reading pages are static SSR since brief 09f: each
/// navigation fetches the new page's HTML, there is no persisted state to restore and no circuit.
/// (Brief 09c wrote these tests for the [PersistentState] handoff, which these pages no longer use.)
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class ReadingPageNavigationTests : WebTest
{
    private static readonly SemaphoreSlim DataGate = new(1, 1);
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];

    private static readonly string PostATitle = $"09c PostA {RunId}";
    private static readonly string PostBTitle = $"09c PostB {RunId}";
    private static readonly string CategoryAName = $"09c CatA {RunId}";
    private static readonly string CategoryBName = $"09c CatB {RunId}";

    private static bool _dataReady;
    private static string _postASlug = string.Empty;
    private static string _postBSlug = string.Empty;
    private static string _categoryASlug = string.Empty;
    private static string _categoryBSlug = string.Empty;

    [Test]
    public async Task PostNavigation_ClientSide_ShowsCorrectContent()
    {
        // Brief 09c I-5: post A full page load → wait for circuit → click link to post B
        // → verify client-side navigation, B's title and body show, no full document load
        await EnsureFixturesAsync();
        await Page.SetViewportSizeAsync(1440, 900);

        // Full page load of post A
        await Page.GotoAsync($"{BaseUrl}/post/{_postASlug}");

        // Wait for interactive circuit (scroll-spy initialization indicates interactive mode)
        await Expect(Page.Locator(".post-rail .toc__link[aria-current='true']"))
            .ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 10000 });

        // Verify post A content is displayed
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = PostATitle, Level = 1 }))
            .ToBeVisibleAsync();
        await Expect(Page.Locator(".markdown-body"))
            .ToContainTextAsync($"This is the intro for Post A {RunId}");

        // Set marker to detect full page load
        await Page.EvaluateAsync("() => { window.__navigationMarker = 'post-a-loaded'; }");

        // Click the link to post B (client-side navigation)
        await Page.Locator(".markdown-body a[href$='/" + _postBSlug + "']").ClickAsync();

        // Wait for URL to change to post B
        await Page.WaitForURLAsync(url => url.EndsWith($"/post/{_postBSlug}"), new PageWaitForURLOptions { Timeout = 10000 });

        // Verify no full page load occurred (marker should still be present)
        var markerValue = await Page.EvaluateAsync<string?>("() => window.__navigationMarker");
        Assert.That(markerValue, Is.EqualTo("post-a-loaded"),
            "Full page load detected - client-side navigation did not occur");

        // Wait for interactive circuit on post B
        await Expect(Page.Locator(".post-rail .toc__link[aria-current='true']"))
            .ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 10000 });

        // Verify post B content is displayed (not post A's cached content)
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = PostBTitle, Level = 1 }))
            .ToBeVisibleAsync();
        await Expect(Page.Locator(".markdown-body"))
            .ToContainTextAsync($"This is the intro for Post B {RunId}");

        // Verify post A's content is NOT displayed
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = PostATitle, Level = 1 }))
            .ToBeHiddenAsync();
    }

    [Test]
    public async Task CategoryNavigation_ClientSide_ShowsCorrectContent()
    {
        // Brief 09c I-5: category A full page load → wait for circuit → click link to category B
        // → verify client-side navigation, correct posts display, no full document load
        await EnsureFixturesAsync();
        await Page.SetViewportSizeAsync(1440, 900);

        // Full page load of category A
        await Page.GotoAsync($"{BaseUrl}/category/{_categoryASlug}");

        // Wait for interactive mode (wait for a post card link to be clickable)
        await Expect(Page.Locator(".post-list a").First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 });

        // Verify category A posts are displayed
        await Expect(Page.Locator(".post-list"))
            .ToContainTextAsync(PostATitle);

        // Set marker to detect full page load
        await Page.EvaluateAsync("() => { window.__navigationMarker = 'category-a-loaded'; }");

        // Click category B link in sidebar (client-side navigation)
        await Page.Locator($"#sidebar-nav a[href='/category/{_categoryBSlug}']").ClickAsync();

        // Wait for URL to change to category B
        await Page.WaitForURLAsync(url => url.EndsWith($"/category/{_categoryBSlug}"), new PageWaitForURLOptions { Timeout = 10000 });

        // Verify no full page load occurred (marker should still be present)
        var markerValue = await Page.EvaluateAsync<string?>("() => window.__navigationMarker");
        Assert.That(markerValue, Is.EqualTo("category-a-loaded"),
            "Full page load detected - client-side navigation did not occur");

        // Wait for interactive mode on category B
        await Expect(Page.Locator(".post-list a").First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 });

        // Verify category B posts are displayed
        await Expect(Page.Locator(".post-list"))
            .ToContainTextAsync(PostBTitle);

        // Verify category A's post is NOT displayed in category B
        await Expect(Page.Locator(".post-list"))
            .Not.ToContainTextAsync(PostATitle);
    }

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

            // Create categories on the tags page (where category management lives)
            await Page.GotoAsync($"{BaseUrl}/admin/tags");
            await WaitForInteractiveAsync(Page);
            await CreateCategoryAsync(CategoryAName);
            await CreateCategoryAsync(CategoryBName);

            // Create post B first (no links needed)
            var postBBody = $@"# Introduction

This is the intro for Post B {RunId}.

## First Section

Content for first section of post B.

## Second Section

Content for second section of post B.";
            _postBSlug = await PublishPostAsync(PostBTitle, postBBody, categoryName: CategoryBName);

            // Create post A with a Markdown link to post B (for client-side navigation test)
            var postABody = $@"# Introduction

This is the intro for Post A {RunId}.

## First Section

Content for first section of post A.

## Second Section

Content for second section of post A.

[Go to Post B](/post/{_postBSlug})";
            _postASlug = await PublishPostAsync(PostATitle, postABody, categoryName: CategoryAName);

            // Get category slugs
            _categoryASlug = await GetCategorySlugAsync(CategoryAName);
            _categoryBSlug = await GetCategorySlugAsync(CategoryBName);

            await Page.GotoAsync($"{BaseUrl}/logout");
            _dataReady = true;
        }
        finally
        {
            DataGate.Release();
        }
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

    private async Task<string> PublishPostAsync(string title, string body, string? tagName = null, string? categoryName = null)
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
            var category = Page.GetByRole(AriaRole.Combobox, new PageGetByRoleOptions { Name = "Category" });
            await category.ClickAsync();
            await category.FillAsync(categoryName);
            await Page.GetByRole(AriaRole.Option, new PageGetByRoleOptions { Name = categoryName }).ClickAsync();
        }

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Publish now" }).ClickAsync();
        await Page.WaitForURLAsync(
            url => url.StartsWith($"{BaseUrl}/admin/posts/") && url.EndsWith("/edit"),
            new PageWaitForURLOptions { Timeout = 15000 });

        return await SlugForPostAsync(PostIdFromEditorUrl());
    }

    private async Task<string> GetCategorySlugAsync(string categoryName)
    {
        // Use the API to get the category by name and extract the slug
        await using var response = await Page.APIRequest.GetAsync($"{ApiUrl}/api/categories?search={Uri.EscapeDataString(categoryName)}");
        Assert.That(response.Status, Is.EqualTo(200), "The categories API did not return successfully.");
        using var doc = JsonDocument.Parse(await response.TextAsync());
        
        // API returns array directly
        foreach (var category in doc.RootElement.EnumerateArray())
        {
            if (category.GetProperty("name").GetString() == categoryName)
            {
                return category.GetProperty("slug").GetString()!;
            }
        }
        
        throw new InvalidOperationException($"Could not find slug for category {categoryName}");
    }
}
