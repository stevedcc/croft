using System.Text.RegularExpressions;

using System.Text.Json;

namespace Croft.Web.E2E;

/// <summary>
/// A full load of a reading page makes exactly ONE API request for its post/category data. Since
/// brief 09f the reading pages are static SSR: they render once on the server and never become
/// interactive, so there is no second (interactive) render that could fetch again. (Brief 09c
/// guarded the same count against the prerender -> interactive handoff with [PersistentState],
/// which these pages no longer use.)
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class ReadingPageApiCountTests : WebTest
{
    private static readonly SemaphoreSlim DataGate = new(1, 1);
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];

    private static readonly string PostTitle = $"09c ApiCount Post {RunId}";
    private static readonly string CategoryName = $"09c ApiCount Cat {RunId}";

    private static bool _dataReady;
    private static string _postSlug = string.Empty;
    private static string _categorySlug = string.Empty;

    [Test]
    public async Task PostPage_FullLoad_MakesExactlyOneApiRequest()
    {
        await EnsureFixturesAsync();
        await Page.SetViewportSizeAsync(1440, 900);

        // The instance's log holds every request since it started, other fixtures' and this
        // fixture's other tests' included: count what this load adds, so test order is irrelevant.
        var before = CountApiRequestsFromWebLog($"posts/{_postSlug}");

        // Full page load
        await Page.GotoAsync($"{BaseUrl}/post/{_postSlug}");

        // Wait for interactive circuit (scroll-spy indicates interactive mode)
        await Expect(Page.Locator(".post-rail .toc__link[aria-current='true']"))
            .ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 10000 });

        // Count the API requests this load made, from the Web app's log
        var requestCount = CountApiRequestsFromWebLog($"posts/{_postSlug}") - before;

        // Assert exactly one API request for this post
        Assert.That(requestCount, Is.EqualTo(1),
            $"Expected exactly 1 API request for /api/posts/{_postSlug}, but found {requestCount}. " +
            "The prerender makes one request, and the interactive render should use restored state.");
    }

    [Test]
    public async Task CategoryPage_FullLoad_MakesExactlyOneApiRequest()
    {
        await EnsureFixturesAsync();
        await Page.SetViewportSizeAsync(1440, 900);
        var before = CountApiRequestsFromWebLog($"categories/{_categorySlug}");

        // Full page load
        await Page.GotoAsync($"{BaseUrl}/category/{_categorySlug}");

        // Wait for Blazor circuit to be live (interactive render has completed)
        // This ensures we count API requests after the interactive render has had a chance
        // to make its API call (if the restore branch is broken)
        await WaitForInteractiveAsync(Page);

        // Count the API requests this load made, from the Web app's log
        var requestCount = CountApiRequestsFromWebLog($"categories/{_categorySlug}") - before;

        // Assert exactly one API request for this category
        Assert.That(requestCount, Is.EqualTo(1),
            $"Expected exactly 1 API request for /api/categories/{_categorySlug}, but found {requestCount}. " +
            "The prerender makes one request, and the interactive render should use restored state.");
    }

    [Test]
    public async Task PostPage_FullLoad_ReadsTheSidebarAndFooterPagesInOneApiRequest()
    {
        // Brief 10f: the footer adds no API call to a reading page. The shell reads the sidebar's
        // pages and the footer's from the one combined endpoint, so a page load costs one
        // navigation request, and nothing asks for the per-placement lists.
        await EnsureFixturesAsync();
        await Page.SetViewportSizeAsync(1440, 900);
        var combinedBefore = CountApiRequestsFromWebLog("pages/navigation/all");
        var singleBefore = CountApiRequestsFromWebLog("pages/navigation");

        await Page.GotoAsync($"{BaseUrl}/post/{_postSlug}");
        await Expect(Page.Locator("footer[role=contentinfo]")).ToBeVisibleAsync();

        Assert.That(CountApiRequestsFromWebLog("pages/navigation/all") - combinedBefore, Is.EqualTo(1),
            "one combined navigation request feeds the sidebar and the footer");
        Assert.That(CountApiRequestsFromWebLog("pages/navigation") - singleBefore, Is.Zero,
            "no separate request for the sidebar's or the footer's list");
    }

    private int CountApiRequestsFromWebLog(string endpointPath)
    {
        // Derive instance number from BaseUrl
        var webPort = new Uri(BaseUrl).Port;
        var instanceNumber = (webPort - 5002) / 10 + 1;
        var logPath = $"/tmp/blog-e2e-{instanceNumber}-web.log";

        if (!File.Exists(logPath))
        {
            TestContext.Progress.WriteLine($"Warning: Web log not found at {logPath}");
            return 0;
        }

        var logContent = File.ReadAllText(logPath);
        // Only count "Start processing HTTP request" lines to avoid double-counting
        // (each request logs both "Start processing" and "Sending" lines)
        // Match exact endpoint: followed by query string or end of line, not by path suffix
        // Use ApiUrl to support multiple E2E instances (ports 5000, 5010, 5020, etc.)
        var pattern = $@"Start processing HTTP request GET {Regex.Escape(ApiUrl)}/api/{Regex.Escape(endpointPath)}(\?.*)?$";
        var matches = Regex.Matches(logContent, pattern, RegexOptions.Multiline);
        
        TestContext.Progress.WriteLine($"Found {matches.Count} API requests for endpoint: {endpointPath}");
        
        return matches.Count;
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

            // Create category on the tags page
            await Page.GotoAsync($"{BaseUrl}/admin/tags");
            await WaitForInteractiveAsync(Page);
            await CreateCategoryAsync(CategoryName);

            // Create post in the category with headings so TOC and scroll-spy work
            var postBody = $@"# Introduction

This is the intro for {RunId}.

## First Section

Content for first section.

## Second Section

Content for second section.";
            _postSlug = await PublishPostAsync(PostTitle, postBody, categoryName: CategoryName);

            // Get category slug
            _categorySlug = await GetCategorySlugAsync(CategoryName);

            await SignOutAsync();
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
