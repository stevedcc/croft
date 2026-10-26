namespace Croft.Web.E2E;

[Parallelizable(ParallelScope.Fixtures)]
public class AuthenticationTests : WebTest
{
    [Test]
    public async Task AdminLogin_ShowsAdminNav()
    {
        await LoginAsync("admin@example.com", "password");

        // Ruling R2-R4: the selectors follow the new sidebar's accessible names (the Admin
        // section is the "Posts" link); the assertions are unchanged — admin sees the Admin section.
        await Expect(Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Posts", Exact = true })).ToBeVisibleAsync();
    }

    [Test]
    public async Task EditorLogin_ShowsEditorNav()
    {
        await LoginAsync("editor@example.com", "password");

        // Ruling R2-R4: the Editor section is the "Drafts" link; the assertions are unchanged —
        // editor sees the Editor section and NOT the Admin section.
        await Expect(Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Drafts" })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Posts", Exact = true })).Not.ToBeVisibleAsync();
    }

    [TestCase("/admin/posts")]
    [TestCase("/admin/posts/new")]
    [TestCase("/admin/pages")]
    [TestCase("/admin/tags")]
    [TestCase("/admin/media")]
    [TestCase("/admin/discussion/blocks")]
    public async Task Editor_OpeningAnAdminPage_IsRefused_AndSeesNoAdminContent(string path)
    {
        // The pages are [Authorize(Roles = "Admin")]: the Editor role is not enough. Until now no test had an
        // Editor open an /admin URL (review 2026-10-19, C-1), so dropping the attribute from a page left every
        // suite green and showed an Editor the admin grid (the API would still refuse its data).
        // What an Editor meets is the authorization middleware's refusal: the cookie handler's default
        // access-denied redirect (/Account/AccessDenied, which no page owns, so the not-found page answers 404).
        // Pinned as it is; the <NotAuthorized> copy in Routes.razor is only for a circuit's own navigation.
        await LoginAsync("editor@example.com", "password");

        var response = await Page.GotoAsync($"{BaseUrl}{path}");

        Assert.That(Page.Url, Does.StartWith($"{BaseUrl}/Account/AccessDenied"), "the Editor is sent to the access-denied path, not served the page");
        Assert.That(response!.Status, Is.EqualTo(404), "and what answers there is the not-found page");
        await Expect(Page.Locator(".posts-grid, .admin-table, .media-grid, .post-form")).ToHaveCountAsync(0);
        await Expect(Page.GetByText("This page has wandered off")).ToBeVisibleAsync();
    }

    [Test]
    public async Task Login_WithExternalReturnUrl_RedirectsToHome()
    {
        // Navigate to /login with an external returnUrl — the open-redirect payload
        await Page.GotoAsync($"{BaseUrl}/login?returnUrl=https://evil.example");
        await Page.WaitForURLAsync(url => url.StartsWith($"{OAuthAuthority}/authorize"), new PageWaitForURLOptions { Timeout = 10000 });

        // Complete the login flow
        await Page.Locator("input[name='username']").FillAsync("admin@example.com");
        await Page.Locator("input[type='submit'][value='Sign-in']").ClickAsync();

        // Wait for the post-login page to settle
        await Page.GetByText("Log out")
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });

        // Assert: the browser must be on localhost, NOT on evil.example
        Assert.That(Page.Url, Does.StartWith(BaseUrl),
            $"Open redirect succeeded — browser landed on {Page.Url} instead of {BaseUrl}");
    }
}
