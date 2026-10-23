using System.Text.RegularExpressions;
using Croft.Shared.Contracts.Social;
using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;

namespace Croft.Web.E2E;

/// <summary>
/// Brief 12c item 5: the owner's side of the discussion, in a real browser: the Discussion fields in the editor, the
/// Discussion tab on the edit page (status, Refresh now, hide reply, hide account), and the Hidden replies page. The E2E
/// instances run with Social:Enabled on and no Mastodon server and no Bluesky DID, so the API's fetcher asks no platform;
/// every test seeds its own post, links and replies straight into the tables (<see cref="DiscussionSeed"/>, invented names,
/// `.example` hosts). No test calls Mastodon, Bluesky or any other real service.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
[Category("DiscussionAdmin")]
public class DiscussionAdminTests : WebTest
{
    private const int Mastodon = 1;

    private static string NewRunId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>
    /// The replies of the hide tests: Ada's has an answer (Bram's), Cleo wrote the other two (the same account, two replies).
    /// Every name carries the run id, so the global blocks list of a shared instance can be read for this run's rows alone.
    /// </summary>
    private static IReadOnlyList<DiscussionSeed.Reply> Thread(string runId) =>
    [
        new(Mastodon, $"ada{runId}", null, $"Ada Example {runId}", $"ada{runId}@social.example", TimeSpan.FromHours(5),
            $"<p>Ada says the garden path {runId} looks wonderful.</p>"),
        new(Mastodon, $"bram{runId}", $"ada{runId}", $"Bram Sample {runId}", $"bram{runId}@social.example", TimeSpan.FromHours(4),
            $"<p>Bram agrees with Ada {runId}.</p>", WithAvatar: true),
        new(Mastodon, $"cleo1{runId}", null, $"Cleo Demo {runId}", $"cleo{runId}@social.example", TimeSpan.FromHours(3),
            $"<p>Cleo first remark {runId}.</p>", Account: $"acct-cleo{runId}"),
        new(Mastodon, $"cleo2{runId}", null, $"Cleo Demo {runId}", $"cleo{runId}@social.example", TimeSpan.FromHours(2),
            $"<p>Cleo second remark {runId}.</p>", Account: $"acct-cleo{runId}"),
    ];

    private async Task<(Guid Id, string Slug)> SeedPostAsync(string runId, bool withReplies = true)
    {
        var (id, slug) = await DiscussionSeed.PublishPostAsync(ApiUrl, $"Moderated post {runId}", $"A post about paths {runId}.");
        await using var db = await OpenInstanceDatabaseAsync();
        var post = await DiscussionSeed.LinkAsync(db, id, slug, runId);
        if (withReplies)
        {
            await DiscussionSeed.AddRepliesAsync(db, InstanceNumber, post, Thread(runId));
        }

        return (id, slug);
    }

    private async Task OpenDiscussionTabAsync(IPage page, Guid postId)
    {
        await page.GotoAsync($"{BaseUrl}/admin/posts/{postId}/edit");
        await WaitForInteractiveAsync(page);
        await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Edit Post", Level = 1 })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Discussion" }).ClickAsync();
        await Expect(page.Locator(".discussion-admin__status")).ToBeVisibleAsync();
    }

    private static ILocator Names(IPage page) => page.Locator(".discussion-admin__reply .discussion-admin__name");

    /// <summary>The post's public page as an anonymous reader gets it: its own request context, so no cookie of the admin goes along.</summary>
    private async Task<string> AnonymousPostHtmlAsync(string slug)
    {
        await using var anonymous = await Playwright.APIRequest.NewContextAsync();
        await using var response = await anonymous.GetAsync($"{BaseUrl}/post/{slug}");
        Assert.That(response.Status, Is.EqualTo(200));
        return await response.TextAsync();
    }

    private async Task ConfirmHideAsync(IPage page, string? note = null)
    {
        var dialog = page.Locator(".mud-dialog");
        await Expect(dialog).ToBeVisibleAsync();
        if (note is not null)
        {
            await dialog.GetByLabel("Note").FillAsync(note);
        }

        await dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Hide", Exact = true }).ClickAsync();
        await Expect(dialog).ToHaveCountAsync(0);
    }

    // ---- the editor's Discussion fields ----

    [Test]
    public async Task TheEditor_ShowsTheDiscussionFieldsForAPost_AndNotForAPage()
    {
        await LoginAsync("admin@example.com", "password");

        await OpenNewPostEditorAsync();
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Discussion" })).ToBeVisibleAsync();
        await Expect(Page.GetByLabel("Mastodon post URL")).ToBeVisibleAsync();
        await Expect(Page.GetByLabel("Bluesky post URL")).ToBeVisibleAsync();
        await Expect(Page.GetByText("The post that announces this one on Mastodon")).ToBeVisibleAsync();
        await Expect(Page.GetByLabel("Mastodon post URL")).ToHaveAttributeAsync("inputmode", "url");

        await Page.GotoAsync($"{BaseUrl}/admin/pages/new");
        await WaitForInteractiveAsync(Page);
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "New Page" })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Discussion" })).ToHaveCountAsync(0);
        await Expect(Page.GetByLabel("Mastodon post URL")).ToHaveCountAsync(0);
        await Expect(Page.GetByLabel("Bluesky post URL")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task AMastodonUrl_IsRefusedUnderItsField_WhenTheSiteHasNoMastodonServer_AndNothingElseChanges()
    {
        var runId = NewRunId();
        await LoginAsync("admin@example.com", "password");
        await OpenNewPostEditorAsync();
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync($"Linked post {runId}");
        await Page.GetByLabel("Markdown").FillAsync($"Body {runId}.");
        var field = Page.GetByLabel("Mastodon post URL");
        await field.FillAsync($"https://social.example/@owner/{runId}");
        await field.BlurAsync();

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save draft" }).ClickAsync();

        // The API's refusal names the field and says the platform is not configured; it appears under the Mastodon field only.
        var message = Page.Locator(".post-form__link", new PageLocatorOptions { HasText = "Mastodon post URL" }).Locator(".mud-input-helper-text");
        await Expect(message).ToContainTextAsync("MastodonUrl");
        await Expect(message).ToContainTextAsync("not configured");
        await Expect(field).ToHaveAttributeAsync("aria-invalid", "true");
        await Expect(Page.Locator(".post-edit__error")).ToHaveCountAsync(0);

        // Nothing was saved or lost: still the new-post editor, with what was typed.
        Assert.That(Page.Url, Does.EndWith("/admin/posts/new"), "a refused save does not leave the new-post editor");
        await Expect(Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true })).ToHaveValueAsync($"Linked post {runId}");
        await Expect(field).ToHaveValueAsync($"https://social.example/@owner/{runId}");

        // Typing in the field withdraws its message.
        await field.PressAsync("End");
        await field.PressSequentiallyAsync("x");
        await Expect(message).Not.ToContainTextAsync("MastodonUrl");
        await Expect(field).Not.ToHaveAttributeAsync("aria-invalid", "true");
    }

    // ---- the Discussion tab ----

    [Test]
    public async Task TheDiscussionTab_ShowsTheStatusAndEveryStoredReply_AndRefreshNowQueuesAFetch()
    {
        var runId = NewRunId();
        var (id, _) = await SeedPostAsync(runId);
        await LoginAsync("admin@example.com", "password");

        await OpenDiscussionTabAsync(Page, id);

        var status = Page.Locator(".discussion-admin__status");
        await Expect(status).ToContainTextAsync("Mastodon: last fetched just now (4 replies stored)");
        await Expect(status).ToContainTextAsync("Bluesky: last fetched just now (0 replies stored)");
        await Expect(status).ToContainTextAsync(new Regex(@"Next fetch in \d+ days"));
        await Expect(Names(Page)).ToHaveTextAsync([$"Ada Example {runId}", $"Bram Sample {runId}", $"Cleo Demo {runId}", $"Cleo Demo {runId}"]);
        await Expect(Page.Locator(".discussion-admin__reply").Nth(1)).ToHaveAttributeAsync("data-depth", "1");
        await Expect(Page.Locator(".discussion-admin__reply").First.Locator(".discussion-admin__time")).ToHaveTextAsync(new Regex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2} UTC$"));
        await Expect(Page.Locator(".discussion-admin__reply").First.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = "View on Mastodon" }))
            .ToHaveAttributeAsync("target", "_blank");
        var avatar = Page.Locator(".discussion-admin__reply").Nth(1).Locator("img");
        await Expect(avatar).ToHaveAttributeAsync("src", $"/social/avatar/{DiscussionSeed.AvatarHash}.webp");
        // An <img> that is merely visible can still be broken: it must have decoded (the stored avatar is a WebP, which the shared PNG helper does not read).
        await Expect(avatar).ToBeVisibleAsync();
        await Page.WaitForFunctionAsync("() => { const i = document.querySelector('.discussion-admin__reply img'); return i && i.complete && i.naturalWidth > 0; }");

        // Refresh now: the API marks the threads due, and the status read again says so (the outcome, not just the message).
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Refresh now" }).ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Status).Filter(new LocatorFilterOptions { HasText = "Refresh queued. New replies appear within a few minutes." })).ToBeVisibleAsync();
        await Expect(status).ToContainTextAsync("Next fetch due now");
    }

    [Test]
    public async Task TheDiscussionTab_OfAPostWithoutLinks_SaysSo()
    {
        var runId = NewRunId();
        var (id, _) = await DiscussionSeed.PublishPostAsync(ApiUrl, $"Unlinked post {runId}", "Just a post.");
        await LoginAsync("admin@example.com", "password");

        await OpenDiscussionTabAsync(Page, id);

        await Expect(Page.Locator(".discussion-admin__status")).ToHaveTextAsync("No Mastodon or Bluesky post is linked. Add one under Discussion in the form.");
        await Expect(Page.GetByText("No replies yet.")).ToBeVisibleAsync();
    }

    [Test]
    public async Task HidingAReplyAndThenAnAccount_TakesThemOffThePanelAndThePublicPage_AndUnhideBringsTheRowBack()
    {
        var runId = NewRunId();
        var (id, slug) = await SeedPostAsync(runId);
        var before = await AnonymousPostHtmlAsync(slug);
        Assert.That(before, Does.Contain($"Ada Example {runId}").And.Contain($"Bram Sample {runId}").And.Contain($"Cleo Demo {runId}"),
            "the reader sees every reply before anything is hidden");
        await LoginAsync("admin@example.com", "password");
        await OpenDiscussionTabAsync(Page, id);

        // Hide reply on Ada's: she and Bram's answer leave the panel and the public page; Cleo's stay.
        await Page.Locator(".discussion-admin__reply", new PageLocatorOptions { HasText = $"Ada Example {runId}" })
            .GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Hide reply" }).ClickAsync();
        await ConfirmHideAsync(Page, "E2E: off topic");
        await Expect(Names(Page)).ToHaveTextAsync([$"Cleo Demo {runId}", $"Cleo Demo {runId}"]);
        await Expect(Page.Locator(".discussion-admin__status")).ToContainTextAsync("Mastodon: last fetched just now (2 replies stored)");
        var afterReply = await AnonymousPostHtmlAsync(slug);
        Assert.That(afterReply, Does.Not.Contain($"Ada Example {runId}").And.Not.Contain($"Bram Sample {runId}"), "the public page no longer shows the hidden reply or its answer");
        Assert.That(afterReply, Does.Contain($"Cleo Demo {runId}"), "and still shows the rest");

        // Hide account on Cleo: both of her replies leave.
        await Page.Locator(".discussion-admin__reply").First.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Hide account" }).ClickAsync();
        await Expect(Page.Locator(".mud-dialog")).ToContainTextAsync("on every post");
        await ConfirmHideAsync(Page);
        await Expect(Page.Locator(".discussion-admin__reply")).ToHaveCountAsync(0);
        await Expect(Page.GetByText("No replies yet.")).ToBeVisibleAsync();
        var afterAccount = await AnonymousPostHtmlAsync(slug);
        Assert.That(afterAccount, Does.Not.Contain($"Cleo Demo {runId}"), "the public page shows none of the hidden account's replies");

        // The Hidden replies page lists both blocks, by their labels.
        await Page.GotoAsync($"{BaseUrl}/admin/discussion/blocks");
        await WaitForInteractiveAsync(Page);
        var replyRow = Page.Locator("tr.admin-table__row", new PageLocatorOptions { HasText = $"@ada{runId}@social.example: Ada says the garden path {runId} looks wonderful." });
        var accountRow = Page.Locator("tr.admin-table__row", new PageLocatorOptions { HasText = $"Cleo Demo {runId} (@cleo{runId}@social.example)" });
        await Expect(replyRow).ToHaveCountAsync(1);
        await Expect(accountRow).ToHaveCountAsync(1);
        await Expect(replyRow.Locator("td").Nth(1)).ToHaveTextAsync("Reply");
        await Expect(replyRow.Locator("td").Nth(3)).ToHaveTextAsync("E2E: off topic");
        await Expect(replyRow.Locator("td").Nth(4)).ToHaveTextAsync(new Regex(@"\S"));
        await Expect(accountRow.Locator("td").Nth(1)).ToHaveTextAsync("Account");

        // Unhide the reply: the row leaves, the account's row stays, and the list stays that way after a reload.
        await replyRow.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Unhide" }).ClickAsync();
        var dialog = Page.Locator(".mud-dialog");
        await Expect(dialog).ToContainTextAsync("The reply comes back at the next fetch, if it still exists.");
        await dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Unhide", Exact = true }).ClickAsync();
        await Expect(replyRow).ToHaveCountAsync(0);
        await Expect(accountRow).ToHaveCountAsync(1);
        await Page.ReloadAsync();
        await WaitForInteractiveAsync(Page);
        await Expect(accountRow).ToHaveCountAsync(1);
        await Expect(replyRow).ToHaveCountAsync(0);
    }

    [Test]
    public async Task At390Wide_NeitherTheDiscussionTabNorTheHiddenRepliesPageScrollsSideways()
    {
        var runId = NewRunId();
        var (id, _) = await SeedPostAsync(runId);
        using (var api = MockIdpApi.CreateClient())
        {
            var token = await MockIdpApi.GetAdminAccessTokenAsync(api);
            await MockIdpApi.PostJsonAsync(api, ApiUrl, "/api/discussion/blocks",
                new CreateSocialBlockRequest(SocialPlatform.Mastodon, SocialBlockKind.Reply, $"r-wide{runId}", $"@wide{runId}@social.example: {new string('w', 80)}…", "A note long enough to wrap in its column on a phone."), token);
        }

        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions { ViewportSize = new() { Width = 390, Height = 844 } });
        var page = await SignedInPageAsync(context);

        await OpenDiscussionTabAsync(page, id);
        await Expect(Names(page).First).ToBeVisibleAsync();
        var tab = await page.EvaluateAsync<int[]>("() => [document.documentElement.scrollWidth, Math.ceil(document.querySelector('.discussion-admin').getBoundingClientRect().right)]");
        await Expect(page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Discussion" })).ToBeVisibleAsync();
        var tabInView = await page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Discussion" }).EvaluateAsync<bool>(
            "el => { const r = el.getBoundingClientRect(); return r.left >= 0 && r.right <= window.innerWidth; }");

        await page.GotoAsync($"{BaseUrl}/admin/discussion/blocks");
        await WaitForInteractiveAsync(page);
        await Expect(page.Locator("tr.admin-table__row").First).ToBeVisibleAsync();
        var hidden = await page.EvaluateAsync<int>("() => document.documentElement.scrollWidth");

        Assert.That(tab[0], Is.LessThanOrEqualTo(390), "the edit page with the Discussion tab does not scroll sideways");
        Assert.That(tab[1], Is.LessThanOrEqualTo(390), "the panel ends inside the viewport");
        Assert.That(tabInView, Is.True, "the Discussion tab header is fully on screen, not scrolled half away");
        Assert.That(hidden, Is.LessThanOrEqualTo(390), "the Hidden replies page does not scroll sideways (its table scrolls inside its own region)");
    }

    // ---- accessibility ----

    private async Task<IPage> SignedInPageAsync(IBrowserContext context)
    {
        var page = await context.NewPageAsync();
        await LoginAsync(page, "admin@example.com", "password");
        return page;
    }

    private static async Task AssertNoSeriousAxeViolationsAsync(IPage page)
    {
        AxeResult axe = await page.RunAxe();
        var serious = axe.Violations.Where(v => v.Impact is "serious" or "critical").ToList();
        var report = string.Join("\n\n", serious.Select(v =>
            $"{v.Id} ({v.Impact}): {v.Help}\n{v.HelpUrl}\n" + string.Join("\n", v.Nodes.Select(n => $"  {n.Target} :: {n.Html}"))));
        Assert.That(serious, Is.Empty, $"serious/critical axe violations on {page.Url}:\n{report}");
    }

    [TestCase("editor", false)]
    [TestCase("editor", true)]
    [TestCase("tab", false)]
    [TestCase("tab", true)]
    [TestCase("hidden", false)]
    [TestCase("hidden", true)]
    public async Task TheDiscussionSurfaces_HaveNoSeriousOrCriticalAxeViolations(string surface, bool dark)
    {
        var runId = NewRunId();
        var (id, _) = await SeedPostAsync(runId);
        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ColorScheme = ColorScheme.Light,
            ViewportSize = new() { Width = 1440, Height = 900 },
        });
        if (dark)
        {
            await context.AddInitScriptAsync("localStorage.setItem('blog-theme', 'dark')");
        }

        var page = await SignedInPageAsync(context);
        switch (surface)
        {
            case "editor":
                await page.GotoAsync($"{BaseUrl}/admin/posts/{id}/edit");
                await WaitForInteractiveAsync(page);
                await Expect(page.GetByLabel("Mastodon post URL")).ToBeVisibleAsync();
                await Expect(page.GetByLabel("Mastodon post URL")).ToHaveValueAsync($"https://social.example/@owner/{runId}");
                break;
            case "tab":
                await OpenDiscussionTabAsync(page, id);
                await Expect(Names(page).First).ToBeVisibleAsync();
                break;
            default:
                using (var hideApi = MockIdpApi.CreateClient())
                {
                    var token = await MockIdpApi.GetAdminAccessTokenAsync(hideApi);
                    await MockIdpApi.PostJsonAsync(hideApi, ApiUrl, "/api/discussion/blocks",
                        new CreateSocialBlockRequest(SocialPlatform.Mastodon, SocialBlockKind.Account, $"acct-axe{runId}", $"Axe Example {runId} (@axe{runId}@social.example)", "a note"), token);
                }

                await page.GotoAsync($"{BaseUrl}/admin/discussion/blocks");
                await WaitForInteractiveAsync(page);
                await Expect(page.Locator("tr.admin-table__row").First).ToBeVisibleAsync();
                break;
        }

        if (dark)
        {
            await WaitForDarkPaletteAsync(page);
        }

        await AssertNoSeriousAxeViolationsAsync(page);
    }
}

/// <summary>
/// Brief 12c item 5, the outcome that needs the Web app's output cache on (dev/run-e2e.sh --output-cache, which the plain run does too):
/// a reply hidden through the admin UI leaves the public post page of this pod at once, because the Web evicts its cached pages after
/// the API accepted the block. With the cache off every read is fresh and this proves nothing, so the test skips itself there.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
[Category("OutputCache")]
public class DiscussionAdminCacheTests : WebTest
{
    [SetUp]
    public void RequireTheCacheOn()
    {
        if (Environment.GetEnvironmentVariable("E2E_OUTPUT_CACHE") != "on")
        {
            Assert.Ignore("needs instances with the output cache on: dev/run-e2e.sh --output-cache");
        }
    }

    [Test]
    public async Task AHiddenReply_LeavesTheCachedPublicPage_AtOnce()
    {
        var runId = Guid.NewGuid().ToString("N")[..12];
        var (id, slug) = await DiscussionSeed.PublishPostAsync(ApiUrl, $"Cached moderated post {runId}", $"A post about paths {runId}.");
        await using (var db = await OpenInstanceDatabaseAsync())
        {
            var post = await DiscussionSeed.LinkAsync(db, id, slug, runId);
            await DiscussionSeed.AddRepliesAsync(db, InstanceNumber, post,
            [
                new(1, $"ada{runId}", null, $"Ada Example {runId}", $"ada{runId}@social.example", TimeSpan.FromHours(5), $"<p>Ada says hello {runId}.</p>"),
                new(1, $"cleo{runId}", null, $"Cleo Demo {runId}", $"cleo{runId}@social.example", TimeSpan.FromHours(3), $"<p>Cleo says hello {runId}.</p>"),
            ]);
        }

        await using var anonymous = await Playwright.APIRequest.NewContextAsync();
        await using (var first = await anonymous.GetAsync($"{BaseUrl}/post/{slug}"))
        {
            Assert.That(await first.TextAsync(), Does.Contain($"Ada Example {runId}"), "the reader sees the reply, and the page is now cached");
        }

        await LoginAsync("admin@example.com", "password");
        await Page.GotoAsync($"{BaseUrl}/admin/posts/{id}/edit");
        await WaitForInteractiveAsync(Page);
        await Page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Discussion" }).ClickAsync();
        await Page.Locator(".discussion-admin__reply", new PageLocatorOptions { HasText = $"Ada Example {runId}" })
            .GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Hide reply" }).ClickAsync();
        await Page.Locator(".mud-dialog").GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Hide", Exact = true }).ClickAsync();
        await Expect(Page.Locator(".discussion-admin__reply")).ToHaveCountAsync(1);

        await using var after = await anonymous.GetAsync($"{BaseUrl}/post/{slug}");
        var html = await after.TextAsync();
        Assert.That(html, Does.Not.Contain($"Ada Example {runId}"), "the public page of this pod no longer shows the hidden reply (its cached copy was evicted)");
        Assert.That(html, Does.Contain($"Cleo Demo {runId}"));
    }
}
