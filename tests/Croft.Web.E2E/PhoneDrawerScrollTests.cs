using System.Text.Json;

namespace Croft.Web.E2E;

/// <summary>
/// Brief 03f item 4: on a short phone viewport the drawer must let the user scroll to its last
/// entry ("Log in") and tap it — the outcome is the mock IdP login page, not just the link being
/// rendered. The regression this guards is the iPhone Safari bug where the drawer's bottom entries
/// sat under Safari's toolbar and the drawer could not scroll: the drawer's own <c>overflow-y: auto</c>
/// is what makes the footer reachable. Break-it-once (mandated by the brief): remove the drawer's
/// <c>overflow-y</c> and this test must fail because "Log in" is then unreachable.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class PhoneDrawerScrollTests : WebTest
{
    // Enough categories (each with a published post) to push the drawer's natural content past a
    // 500px viewport, so the footer ("Log in") starts below the fold and genuinely needs scrolling.
    private const int FixtureCategoryCount = 8;

    private static readonly SemaphoreSlim DataGate = new(1, 1);
    private static bool _dataReady;

    [Test]
    public async Task Phone_ShortViewport_DrawerScrollsToLogIn_OpensMockLoginPage()
    {
        await EnsureDrawerDataAsync();

        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new() { Width = 390, Height = 500 }
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync(BaseUrl);
        await WaitForInteractiveAsync(page);

        await page.Locator(".topbar").GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Menu" }).ClickAsync();
        await page.WaitForFunctionAsync(
            "() => { const el = document.querySelector('#sidebar-nav'); return el && Math.abs(el.getBoundingClientRect().x) < 1; }");

        var login = page.Locator("#sidebar-nav a[href='/login']");
        await login.ScrollIntoViewIfNeededAsync();
        await login.ClickAsync();

        // Outcome: the server's /login endpoint sends the browser to the mock IdP's authorize page
        // and its login form appears (the same gate as SidebarAuthLinksTests).
        await page.WaitForURLAsync(
            url => url.StartsWith($"{OAuthAuthority}/authorize"),
            new PageWaitForURLOptions { Timeout = 10000 });
        await page.Locator("input[name='username']").WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });
    }

    /// <summary>
    /// Creates enough published categories to overflow the 500px drawer, once per test run. Uses the
    /// mock IdP's authorization-code flow directly (the same token flow as dev/seed-dev-data.sh) so
    /// the fixture goes through the API as the mock admin, never through the browser UI.
    /// </summary>
    private async Task EnsureDrawerDataAsync()
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

            using var client = MockIdpApi.CreateClient();
            var token = await MockIdpApi.GetAdminAccessTokenAsync(client);

            // Category creation is idempotent by slug (CategoryService.CreateAsync returns the
            // existing category), so this is safe against a database other tests already filled.
            var categoryNames = Enumerable.Range(1, FixtureCategoryCount).Select(i => $"Drawer Fixture {i}").ToArray();
            foreach (var name in categoryNames)
            {
                var category = await MockIdpApi.PostJsonAsync(client, ApiUrl, "/api/categories", JsonSerializer.Serialize(name), token);
                var categoryId = category.RootElement.GetProperty("id").GetString()!;
                var post = await MockIdpApi.PostJsonAsync(client, ApiUrl, "/api/posts", JsonSerializer.Serialize(new
                {
                    title = $"{name} Post",
                    markdownContent = "Fixture post so this category shows in the phone drawer.",
                    categoryId
                }), token);
                var postId = post.RootElement.GetProperty("id").GetString()!;
                await MockIdpApi.PostJsonAsync(client, ApiUrl, $"/api/posts/{postId}/publish", "{}", token);
            }

            _dataReady = true;
        }
        finally
        {
            DataGate.Release();
        }
    }
}
