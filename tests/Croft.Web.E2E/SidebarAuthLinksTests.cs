using System.Text.RegularExpressions;

namespace Croft.Web.E2E;

/// <summary>
/// The sidebar's "Log in" and "Log out" links target the server's /login and /logout endpoints
/// (MapGet in Program.cs), not Blazor routes: without Blazor's enhanced-navigation opt-out the
/// router swallows the click and renders the not-found page. The test drives the full round trip
/// by clicking the sidebar links and asserts the outcomes: the mock IdP's login page appears, the
/// admin section appears after signing in as admin, and logout ends at the blog's home page
/// logged out, with the browser's end-session request carrying the ID token as id_token_hint.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class SidebarAuthLinksTests : WebTest
{
    [Test]
    public async Task SidebarLogInAndLogOutLinks_CompleteAFullLoginLogoutRoundTrip()
    {
        await Page.GotoAsync(BaseUrl);
        await WaitForInteractiveAsync(Page);

        await Page.Locator("#sidebar-nav a[href='/login']").ClickAsync();

        // Outcome 1: the mock IdP's login page appears. Only the server's /login endpoint (a full
        // page load) can send the browser there; an enhanced-navigation click never leaves the app.
        await Page.WaitForURLAsync(
            url => url.StartsWith($"{OAuthAuthority}/authorize"),
            new PageWaitForURLOptions { Timeout = 10000 });
        await Page.Locator("input[name='username']").WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });

        await Page.Locator("input[name='username']").FillAsync("admin@example.com");
        await Page.Locator("input[type='submit'][value='Sign-in']").ClickAsync();

        // Back on the app after the IdP leg (the shared LoginAsync helper allows the same chain
        // 10 s; the default 5 s expect bound is too tight for a loaded shared IdP).
        await Page.WaitForURLAsync(url => url.StartsWith(BaseUrl), new PageWaitForURLOptions { Timeout = 30000 });

        // Outcome 2: the admin section appears (its "Posts" link, the same gate as AuthenticationTests).
        await Expect(Page.Locator("#sidebar-nav").GetByRole(
            AriaRole.Link, new LocatorGetByRoleOptions { Name = "Posts", Exact = true })).ToBeVisibleAsync(new() { Timeout = 30000 });
        await Expect(Page.Locator("#sidebar-nav a[href='/logout']")).ToBeVisibleAsync(new() { Timeout = 30000 });

        await WaitForInteractiveAsync(Page);
        var endSessionUrls = new List<string>();
        Page.Request += (_, request) =>
        {
            if (request.Url.Contains("/endsession", StringComparison.Ordinal))
            {
                lock (endSessionUrls)
                {
                    endSessionUrls.Add(request.Url);
                }
            }
        };
        await Page.Locator("#sidebar-nav a[href='/logout']").ClickAsync();

        // Outcome 3: logout ends at the blog's home page, logged out. The browser goes through the
        // IdP's end-session endpoint and back via /signout-callback-oidc, so "Log in" is back,
        // "Log out" is gone, and the final URL is exactly the blog's home page.
        await Expect(Page.Locator("#sidebar-nav a[href='/login']")).ToBeVisibleAsync(new() { Timeout = 30000 });
        await Expect(Page.Locator("#sidebar-nav a[href='/logout']")).ToHaveCountAsync(0, new() { Timeout = 30000 });
        Assert.That(Page.Url, Is.EqualTo($"{BaseUrl}/"),
            $"Logout did not end at the blog's home page: {Page.Url}");

        // The IdP session can only end when the browser's end-session request carries the ID token
        // as id_token_hint — the OIDC provider answers 400 without it and keeps its own session alive.
        string[] sent;
        lock (endSessionUrls)
        {
            sent = [.. endSessionUrls];
        }
        Assert.That(sent, Has.Length.EqualTo(1),
            $"Logout must send exactly one end-session request; it sent {sent.Length}: {string.Join(", ", sent)}");
        var endSession = sent[0];
        Assert.That(Regex.IsMatch(new Uri(endSession!).Query, @"[?&]id_token_hint=[^&]+"), Is.True,
            $"The end-session request carried no id_token_hint: {endSession}");
    }

    /// <summary>
    /// Sign-out works without JavaScript (brief 13b): the interstitial is a POST form with an
    /// antiforgery token and a visible button, so the user finishes the sign-out by hand and ends
    /// at the blog's home page, logged out, through the IdP's end-session endpoint. The POST answers a
    /// 200 page that continues by meta refresh, because Chromium blocks a form POST's redirect to
    /// another origin under form-action 'self' (W-096).
    /// </summary>
    [Test]
    public async Task SignOutWithoutJavaScript_TheVisibleButtonCompletesIt()
    {
        // The mock IdP's login page needs JavaScript, so sign in with it on and carry the session
        // (cookies) into a context without it.
        string state;
        await using (var signedIn = await Browser.NewContextAsync())
        {
            var loginPage = await signedIn.NewPageAsync();
            await LoginAsync(loginPage, "admin@example.com", "password");
            state = await signedIn.StorageStateAsync();
        }

        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions { JavaScriptEnabled = false, StorageState = state });
        var page = await context.NewPageAsync();

        var endSessionUrls = new List<string>();
        page.Request += (_, request) =>
        {
            if (request.Url.Contains("/endsession", StringComparison.Ordinal))
            {
                lock (endSessionUrls)
                {
                    endSessionUrls.Add(request.Url);
                }
            }
        };

        await page.GotoAsync($"{BaseUrl}/logout");
        await page.WaitForURLAsync(url => url == $"{BaseUrl}/logout/interstitial", new PageWaitForURLOptions { Timeout = 10000 });
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Continue signing out" }).ClickAsync();

        await page.WaitForURLAsync(url => url == $"{BaseUrl}/", new PageWaitForURLOptions { Timeout = 30000 });
        await Expect(page.Locator("#sidebar-nav a[href='/login']")).ToBeVisibleAsync(new() { Timeout = 30000 });
        await Expect(page.Locator("#sidebar-nav a[href='/logout']")).ToHaveCountAsync(0, new() { Timeout = 30000 });
        string[] sent;
        lock (endSessionUrls)
        {
            sent = [.. endSessionUrls];
        }
        Assert.That(sent, Has.Length.EqualTo(1), $"The sign-out must reach the IdP's end-session endpoint once; it sent {sent.Length}.");
        Assert.That(Regex.IsMatch(new Uri(sent[0]).Query, @"[?&]id_token_hint=[^&]+"), Is.True,
            $"The end-session request carried no id_token_hint: {sent[0]}");
    }
}
