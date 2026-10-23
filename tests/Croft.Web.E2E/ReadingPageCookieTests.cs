using Croft.Shared.Enums;

namespace Croft.Web.E2E;

/// <summary>
/// Brief 10c item 5: a reading page sets no cookie on an anonymous visitor. The responses a
/// browser really gets for the home page, a post, a category, a tag, the categories list, a static
/// page, search (with and without a query) and the not-found document of a missing post carry no
/// Set-Cookie header, and the browser's cookie jar stays empty after browsing them (the site's
/// croft_affinity cookie comes from the Traefik ingress and is not in this stack). An admin page
/// still sets the antiforgery cookie. Any engine: the assertions are on the HTTP responses and the
/// context's cookie jar.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class ReadingPageCookieTests : StaticReadingTest
{
    private static readonly SemaphoreSlim DataGate = new(1, 1);
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];

    private static bool _dataReady;
    private static string _postSlug = string.Empty;
    private static string _categorySlug = string.Empty;
    private static string _tagSlug = string.Empty;
    private static string _pageSlug = string.Empty;

    [Test]
    public async Task EveryReadingPage_RespondsWithoutASetCookie_AndTheBrowserHoldsNoCookie()
    {
        await EnsureFixturesAsync();
        string[] paths =
        [
            "/", $"/post/{_postSlug}", $"/category/{_categorySlug}", $"/tag/{_tagSlug}", "/categories",
            $"/{_pageSlug}", "/search", $"/search?q={RunId}", $"/post/missing-{RunId}"
        ];

        foreach (var path in paths)
        {
            var response = await Page.GotoAsync($"{BaseUrl}{path}");
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            var headers = await response!.AllHeadersAsync();
            Assert.That(headers.Keys, Has.None.EqualTo("set-cookie"),
                $"{path}: a reading page must not set a cookie; got {headers.GetValueOrDefault("set-cookie")}");
        }

        Assert.That(await Context.CookiesAsync(), Is.Empty,
            "after browsing every reading page the browser holds no cookie from the site");
    }

    [Test]
    public async Task AdminPage_StillIssuesTheAntiforgeryCookie()
    {
        if (Browser.BrowserType.Name == "webkit")
        {
            Assert.Ignore("WebKit cannot sign in over plain http://localhost (Secure correlation cookie).");
        }

        await LoginAsync("admin@example.com", "password");
        // The sign-in is several documents, any of which may have set the antiforgery cookie: clear it,
        // so that the cookie found afterwards can only have come from the /admin/posts response itself
        // (the reading pages' negative test reads the response's Set-Cookie the same way).
        await Context.ClearCookiesAsync(new BrowserContextClearCookiesOptions
        {
            NameRegex = new System.Text.RegularExpressions.Regex(@"^\.AspNetCore\.Antiforgery\."),
        });
        Assert.That((await Context.CookiesAsync()).Select(c => c.Name), Has.None.StartsWith(".AspNetCore.Antiforgery."),
            "the cookie jar is clear of antiforgery before the admin document loads");

        var response = await Page.GotoAsync($"{BaseUrl}/admin/posts");

        var headers = await response!.AllHeadersAsync();
        Assert.That(headers.GetValueOrDefault("set-cookie") ?? string.Empty, Does.Contain(".AspNetCore.Antiforgery."),
            $"the /admin/posts response itself issues the antiforgery cookie; response status {response.Status}");
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

            var category = await CreateCategoryAsync($"Cookie Cat {RunId}");
            _categorySlug = category.Slug;
            _tagSlug = $"cookie{RunId}";
            var post = await PublishAsync($"Cookie Post {RunId}", BodyWithSections(RunId, 2), [_tagSlug], category.Id);
            _postSlug = post.Slug;
            var page = await PublishAsync($"Cookie About {RunId}", $"Pagemarker {RunId} waits.", kind: PostKind.Page);
            _pageSlug = page.Slug;
            _dataReady = true;
        }
        finally
        {
            DataGate.Release();
        }
    }
}
