using System.Text.RegularExpressions;
using Croft.Shared.Contracts;

namespace Croft.Web.E2E;

/// <summary>
/// Brief 13g item 5: the accessible names and landmarks a screen reader gets, asserted from the
/// browser's own accessibility tree (Playwright's role locators): the posts grid's category and row
/// buttons, the contents-panel toggle before and after post.js runs, the footer outside main, and a
/// single h1 on the post editor. The scan matrix covers heading order, landmarks and button names
/// page by page.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class AccessibleNamesTests : StaticReadingTest
{
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];

    [Test]
    public async Task PostsGrid_NamesTheCategory_AndEveryRowButtonWithThePostTitle()
    {
        var category = await CreateCategoryAsync($"Grid Cat {RunId}");
        var title = $"Grid Post {RunId}";
        await PublishAsync(title, $"Grid body {RunId}.", categoryId: category.Id);

        await LoginAsync("admin@example.com", "password");
        await Page.GotoAsync($"{BaseUrl}/admin/posts");
        await WaitForInteractiveAsync(Page);
        var search = Page.GetByRole(AriaRole.Textbox, new PageGetByRoleOptions { Name = "Search titles" });
        await search.FillAsync(title);
        await search.PressAsync("Enter");

        var row = Page.Locator(".posts-grid__row", new PageLocatorOptions { HasText = title });
        await Expect(row).ToHaveCountAsync(1);
        foreach (var name in new[] { $"Edit {title}", $"Analytics for {title}", $"Feedback for {title}", $"Delete {title}" })
        {
            await Expect(row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = name, Exact = true })).ToHaveCountAsync(1);
        }

        await Expect(row.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = $"View {title}", Exact = true })).ToHaveCountAsync(1);
        await Expect(row.Locator(".posts-grid__category")).ToHaveTextAsync($"Grid Cat {RunId}");
    }

    [Test]
    public async Task ContentsToggle_IsNamedContents_WithoutPostJs_AndAfterIt()
    {
        var post = await PublishAsync($"Contents Post {RunId}", BodyWithSections(RunId, 8));

        // post.js blocked: the toggle must be named by the server's markup alone.
        await using var blocked = await NewPhoneContextAsync();
        await blocked.RouteAsync(new Regex(@"post[^/]*\.js(\?.*)?$"), route => route.AbortAsync());
        var blockedPage = await blocked.NewPageAsync();
        await blockedPage.GotoAsync($"{BaseUrl}/post/{post.Slug}");
        await Expect(blockedPage.Locator(".markdown-body")).ToBeVisibleAsync();
        await Expect(blockedPage.Locator(".toc-inline__row-text")).ToHaveTextAsync(string.Empty);
        await Expect(blockedPage.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Contents", Exact = true, IncludeHidden = true }))
            .ToHaveCountAsync(1);

        // post.js running: the visible text is the current section, the name stays "Contents".
        await Page.SetViewportSizeAsync(390, 844);
        await Page.GotoAsync($"{BaseUrl}/post/{post.Slug}");
        await WaitForInteractiveAsync(Page);
        await Page.EvaluateAsync("() => document.getElementById('section-4').scrollIntoView()");
        await Expect(Page.Locator(".toc-inline__row-text")).ToHaveTextAsync("Section 4");
        await Expect(Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Contents", Exact = true })).ToBeVisibleAsync();
    }

    [TestCase(1440, 900)]
    [TestCase(390, 844)]
    public async Task Footer_IsOutsideMain_BelowTheContent_AndInsideTheViewportWidth(int width, int height)
    {
        await PublishAsync($"Footer Post {RunId}", $"Footer body {RunId}.");
        await Page.SetViewportSizeAsync(width, height);
        await Page.GotoAsync(BaseUrl);
        await WaitForInteractiveAsync(Page);

        await Expect(Page.Locator("main footer")).ToHaveCountAsync(0);
        var footer = Page.GetByRole(AriaRole.Contentinfo);
        await Expect(footer).ToHaveCountAsync(1);
        await Expect(footer).ToBeVisibleAsync();

        var json = await Page.EvaluateAsync<string>(
            @"() => {
                const f = document.querySelector('footer[role=contentinfo]').getBoundingClientRect();
                const m = document.querySelector('main').getBoundingClientRect();
                const last = [...document.querySelectorAll('main .post-card')].pop().getBoundingClientRect();
                return JSON.stringify({ footerTop: f.top + scrollY, footerLeft: f.left, footerRight: f.right, mainBottom: m.bottom + scrollY, mainLeft: m.left, lastCardBottom: last.bottom + scrollY,
                    docWidth: document.documentElement.scrollWidth, viewport: innerWidth });
            }");
        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        var r = doc.RootElement;
        Assert.That(r.GetProperty("footerTop").GetDouble(), Is.GreaterThanOrEqualTo(r.GetProperty("mainBottom").GetDouble() - 1),
            "the footer starts where main ends");
        Assert.That(r.GetProperty("footerTop").GetDouble(), Is.GreaterThanOrEqualTo(r.GetProperty("lastCardBottom").GetDouble()),
            "the footer sits below the last post card");
        Assert.That(r.GetProperty("footerLeft").GetDouble(), Is.GreaterThanOrEqualTo(r.GetProperty("mainLeft").GetDouble()),
            "the footer lines up with the content column, not under the sidebar");
        Assert.That(r.GetProperty("footerRight").GetDouble(), Is.LessThanOrEqualTo(r.GetProperty("viewport").GetDouble()));
        Assert.That(r.GetProperty("docWidth").GetInt32(), Is.LessThanOrEqualTo(width), "no horizontal scroll");
    }

    [Test]
    public async Task PostEditor_HasOneH1_WithThePreviewOpen()
    {
        using var client = MockIdpApi.CreateClient();
        var token = await MockIdpApi.GetAdminAccessTokenAsync(client);
        var draft = await MockIdpApi.PostJsonAsync(client, ApiUrl, "/api/posts",
            new CreatePostRequest($"One H1 {RunId}", "Body.", Subtitle: "Sub"), token);
        var id = draft.RootElement.GetProperty("id").GetString();

        await LoginAsync("admin@example.com", "password");
        await Page.GotoAsync($"{BaseUrl}/admin/posts/{id}/edit");
        await WaitForInteractiveAsync(Page);
        await Expect(Page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Preview" })).ToBeVisibleAsync();
        await Expect(Page.Locator(".post-hero__title")).ToHaveTextAsync($"One H1 {RunId}");

        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Level = 1 })).ToHaveCountAsync(1);
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Level = 1 })).ToHaveTextAsync("Edit Post");
    }
}
