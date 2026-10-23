using System.Text.RegularExpressions;
using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;

namespace Croft.Web.E2E;

/// <summary>
/// Brief 12b item 5: the discussion under a post, in a real browser. The E2E instances run with Social:Enabled on and no Mastodon
/// server and no Bluesky DID, so the API's fetcher asks no platform; every test seeds its own post with both announcement links
/// and its own replies straight into brief 12a's tables (<see cref="DiscussionSeed"/>), the threads' last success set to now (the
/// 48-hour rule). Invented names and texts; no test calls Mastodon, Bluesky or any other real service.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
[Category("Discussion")]
public class DiscussionTests : WebTest
{
    private static string NewRunId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>A published post linked to both announcements, with the full thread (or none).</summary>
    private async Task<DiscussionSeed.SeededPost> SeedPostAsync(string runId, bool withReplies = true)
    {
        var (id, slug) = await DiscussionSeed.PublishPostAsync(ApiUrl, $"Discussion post {runId}", $"A post about paths {runId}.\n\n## A section\n\nSome more text.");
        await using var db = await OpenInstanceDatabaseAsync();
        var post = await DiscussionSeed.LinkAsync(db, id, slug, runId);
        if (withReplies)
        {
            await DiscussionSeed.AddRepliesAsync(db, InstanceNumber, post, DiscussionSeed.TheThread(runId));
        }

        return post;
    }

    private static ILocator Top(IPage page) => page.Locator("#discussion ol.discussion__list > li > article");

    [Test]
    public async Task TheThread_ShowsBothPlatforms_InTimeOrder_WithCountAndNesting()
    {
        var runId = NewRunId();
        var post = await SeedPostAsync(runId);
        var requests = new List<string>();
        Page.Request += (_, request) => requests.Add(request.Url);

        await Page.GotoAsync($"{BaseUrl}/post/{post.Slug}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var section = Page.Locator("#discussion");
        await WaitedFor("the discussion section", () => Expect(section.Locator("h2")).ToHaveTextAsync("Discussion"));
        await Expect(section).ToHaveAttributeAsync("data-nosnippet", "");
        await Expect(section.Locator(".discussion__count")).ToHaveTextAsync("5 replies");
        await Expect(Top(Page)).ToHaveCountAsync(4);
        await Expect(Top(Page).Locator(".discussion-reply__name")).ToHaveTextAsync(
            ["Cleo Demo", "Eli Placeholder", "Ada Example", "Dov Test"]);
        await Expect(Top(Page).Locator(".discussion-reply__platform")).ToHaveTextAsync(
            ["Mastodon", "Bluesky", "Mastodon", "Mastodon"]);
        await Expect(Top(Page).Locator(".discussion-reply__handle")).ToHaveTextAsync(
            ["@cleo@social.example", "@eli.example", "@ada@social.example", "@dov@social.example"]);
        await Expect(Top(Page).First.Locator("time")).ToHaveTextAsync("5 hours ago");
        await Expect(Top(Page).First.Locator("time")).ToHaveAttributeAsync("title", new Regex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2} UTC$"));

        // Ada's answer is nested under her reply, not listed at the top.
        var ada = section.Locator("ol.discussion__list > li").Nth(2);
        await Expect(ada.Locator("ol.discussion-reply__answers .discussion-reply__name")).ToHaveTextAsync("Bram Sample");
        await Expect(ada.Locator("ol.discussion-reply__answers a[href='https://example.org/paths']")).ToHaveAttributeAsync("rel", "nofollow ugc noopener");

        // Every reply links its original (DOM order is the tree order: the three oldest, Ada's answer, Dov).
        var thread = DiscussionSeed.TheThread(runId);
        var views = section.Locator("a.discussion-reply__view");
        await Expect(views).ToHaveTextAsync(["View on Mastodon", "View on Bluesky", "View on Mastodon", "View on Mastodon", "View on Mastodon"]);
        for (var i = 0; i < thread.Count; i++)
        {
            await Expect(views.Nth(i)).ToHaveAttributeAsync("href", DiscussionSeed.Permalink(thread[i]));
        }

        await Expect(section.Locator("a.discussion-reply__view:not([rel='nofollow ugc noopener'])")).ToHaveCountAsync(0);

        // No script, no third party, no cookie: everything the page asked for came from our origin.
        Assert.That(requests.Where(url => !url.StartsWith(BaseUrl, StringComparison.Ordinal) && !url.StartsWith("data:", StringComparison.Ordinal)),
            Is.Empty, "the discussion page makes no third-party request");
        Assert.That(await Context.CookiesAsync(), Is.Empty, "a reader gets no cookie");
    }

    [Test]
    public async Task TheContentWarning_IsFolded_UntilTheReaderOpensIt()
    {
        var post = await SeedPostAsync(NewRunId());
        await Page.GotoAsync($"{BaseUrl}/post/{post.Slug}");

        var details = Page.Locator("#discussion details");
        await Expect(details).ToHaveCountAsync(1);
        await Expect(details.Locator("summary")).ToHaveTextAsync("CW: Spoilers about the ending");
        var hidden = details.GetByText("The hidden twist is that the gardener did it.");
        await Expect(hidden).Not.ToBeVisibleAsync();
        Assert.That(await details.EvaluateAsync<bool>("el => el.open"), Is.False, "the warning starts closed");

        await details.Locator("summary").ClickAsync();

        await Expect(hidden).ToBeVisibleAsync();
        Assert.That(await details.EvaluateAsync<bool>("el => el.open"), Is.True);
    }

    [Test]
    public async Task AReplyWithMedia_SaysSo_AndLinksTheOriginal()
    {
        var runId = NewRunId();
        var post = await SeedPostAsync(runId);
        await Page.GotoAsync($"{BaseUrl}/post/{post.Slug}");

        var dov = Top(Page).Filter(new LocatorFilterOptions { HasText = "Dov Test" });
        var note = dov.Locator(".discussion-reply__media");
        await Expect(note).ToContainTextAsync("Has media: view on Mastodon");
        await Expect(note.Locator("a")).ToHaveAttributeAsync("href", DiscussionSeed.Permalink(DiscussionSeed.TheThread(runId)[4]));
    }

    [Test]
    public async Task TheAvatars_ComeFromOurOrigin_AndTheFallbackIsTheInitial()
    {
        var post = await SeedPostAsync(NewRunId());
        await Page.GotoAsync($"{BaseUrl}/post/{post.Slug}");

        var bram = Page.Locator("#discussion ol.discussion-reply__answers article");
        var img = bram.Locator("img");
        await Expect(img).ToHaveAttributeAsync("src", $"/social/avatar/{post.AvatarHash}.webp");
        await Expect(img).ToHaveAttributeAsync("alt", "");
        // An <img> that is merely visible can still be broken: it must have decoded, and the proxy must have answered with the image.
        await Expect(img).ToBeVisibleAsync();
        await Page.WaitForFunctionAsync("sel => { const i = document.querySelector(sel); return i && i.complete && i.naturalWidth > 0; }",
            "#discussion ol.discussion-reply__answers article img");
        await using var response = await Page.APIRequest.GetAsync($"{BaseUrl}/social/avatar/{post.AvatarHash}.webp");
        Assert.That(response.Status, Is.EqualTo(200));
        Assert.That(response.Headers["content-type"], Is.EqualTo("image/webp"));
        Assert.That(response.Headers["cache-control"], Does.Contain("immutable").And.Contain("max-age=31536000"));

        // A reply without an avatar shows the first letter of the name in a neutral circle, hidden from assistive technology.
        var ada = Top(Page).Filter(new LocatorFilterOptions { HasText = "Ada Example" });
        await Expect(ada.Locator(".discussion-reply__initial")).ToHaveTextAsync("A");
        await Expect(ada.Locator(".discussion-reply__initial")).ToHaveAttributeAsync("aria-hidden", "true");
        await Expect(ada.Locator("img")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task TheMastodonForm_LeadsToALinkOnTheReadersServer_WithoutNavigatingOffSite()
    {
        var post = await SeedPostAsync(NewRunId());
        await Page.GotoAsync($"{BaseUrl}/post/{post.Slug}");

        await Page.GetByLabel("Your Mastodon server").FillAsync("@reader@mastodon.example");
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Reply", Exact = true }).ClickAsync();

        await Page.WaitForURLAsync(new Regex(@"/discussion/mastodon/reply\?"));
        var link = Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Open the post on mastodon.example" });
        await Expect(link).ToHaveAttributeAsync("href",
            $"https://mastodon.example/authorize_interaction?uri={Uri.EscapeDataString(post.MastodonUrl)}");
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Continue to mastodon.example" })).ToBeVisibleAsync();
        Assert.That(Page.Url, Does.StartWith(BaseUrl), "nothing navigated off-site by itself");
    }

    [Test]
    public async Task ABadServerName_Is400_WithTheFormAgain()
    {
        var post = await SeedPostAsync(NewRunId());
        await Page.GotoAsync($"{BaseUrl}/post/{post.Slug}");
        await Page.GetByLabel("Your Mastodon server").FillAsync("mastodon");

        var response = await Page.RunAndWaitForResponseAsync(
            async () => await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Reply", Exact = true }).ClickAsync(),
            r => r.Url.Contains("/discussion/mastodon/reply", StringComparison.Ordinal));

        Assert.That(response.Status, Is.EqualTo(400));
        await Expect(Page.GetByText("That doesn't look like a server name")).ToBeVisibleAsync();
        await Expect(Page.GetByLabel("Your Mastodon server")).ToHaveValueAsync("mastodon");
        await Expect(Page.Locator("a[href*='authorize_interaction']")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task ALinkedPostWithoutReplies_ShowsNoRepliesYet_AndBothActions()
    {
        var runId = NewRunId();
        var post = await SeedPostAsync(runId, withReplies: false);
        await Page.GotoAsync($"{BaseUrl}/post/{post.Slug}");

        await Expect(Page.Locator("#discussion")).ToContainTextAsync("No replies yet");
        await Expect(Page.Locator("#discussion .discussion__count")).ToHaveCountAsync(0);
        await Expect(Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Reply on Bluesky" }))
            .ToHaveAttributeAsync("href", post.BlueskyUrl);
        await Expect(Page.GetByLabel("Your Mastodon server")).ToBeVisibleAsync();
    }

    [Test]
    public async Task APostWithoutLinks_HasNoDiscussionSection()
    {
        var runId = NewRunId();
        var (_, slug) = await DiscussionSeed.PublishPostAsync(ApiUrl, $"Unlinked post {runId}", "Just a post.");

        await Page.GotoAsync($"{BaseUrl}/post/{slug}");

        await Expect(Page.Locator(".markdown-body")).ToContainTextAsync("Just a post.");
        await Expect(Page.Locator("#discussion")).ToHaveCountAsync(0);
    }

    [Test]
    public void TheE2EInstance_RunsWithSocialOnAndNoPlatform_SoItFetchesNothing()
    {
        // Enabled, with no Mastodon server and no Bluesky DID: the API says so at start, once per platform, and its fetcher
        // runs for no platform, so no test can reach a real one (the API tests pin that the tick then asks no one).
        var log = ApiLogText();

        Assert.That(log, Does.Contain("Mastodon is disabled because Social:Mastodon:Server is empty"));
        Assert.That(log, Does.Contain("Bluesky is disabled because Social:Bluesky:Did is empty"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task APostWithADiscussion_HasNoSeriousOrCriticalAxeViolations(bool dark)
    {
        var post = await SeedPostAsync(NewRunId());
        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ColorScheme = ColorScheme.Light,
            ViewportSize = new() { Width = 1440, Height = 900 },
        });
        if (dark)
        {
            await context.AddInitScriptAsync("localStorage.setItem('blog-theme', 'dark')");
        }

        var page = await context.NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/post/{post.Slug}");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(page.Locator("#discussion .discussion-reply__name").First).ToBeVisibleAsync();
        if (dark)
        {
            await WaitForDarkPaletteAsync(page);
        }

        AxeResult axe = await page.RunAxe();
        var serious = axe.Violations.Where(v => v.Impact is "serious" or "critical").ToList();
        var report = string.Join("\n\n", serious.Select(v =>
            $"{v.Id} ({v.Impact}): {v.Help}\n{v.HelpUrl}\n" + string.Join("\n", v.Nodes.Select(n => $"  {n.Target} :: {n.Html}"))));
        Assert.That(serious, Is.Empty, $"serious/critical axe violations on {page.Url}:\n{report}");
    }

    [Test]
    public async Task At390Wide_TheDiscussionDoesNotOverflowHorizontally()
    {
        var post = await SeedPostAsync(NewRunId());
        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new() { Width = 390, Height = 844 },
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/post/{post.Slug}");
        await Expect(page.Locator("#discussion .discussion-reply__name").First).ToBeVisibleAsync();

        var widths = await page.EvaluateAsync<int[]>(
            "() => [document.documentElement.scrollWidth, Math.ceil(document.querySelector('#discussion').getBoundingClientRect().right)]");

        Assert.That(widths[0], Is.LessThanOrEqualTo(390), "the page does not scroll sideways");
        Assert.That(widths[1], Is.LessThanOrEqualTo(390), "the section ends inside the viewport");
    }

    [Test]
    [Category("Csp")]
    public async Task APostWithADiscussion_LoadsUnderTheEnforcedPolicy_ZeroViolations()
    {
        var post = await SeedPostAsync(NewRunId());
        var probe = await CspViolationProbe.AttachAsync(Page);

        await probe.StepAsync("post with a discussion");
        await Page.GotoAsync($"{BaseUrl}/post/{post.Slug}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.Locator("#discussion .discussion-reply__name").First).ToBeVisibleAsync();
        await Page.WaitForFunctionAsync("() => [...document.querySelectorAll('#discussion img')].every(i => i.complete && i.naturalWidth > 0)");

        await probe.StepAsync("the Mastodon reply page");
        await Page.GetByLabel("Your Mastodon server").FillAsync("mastodon.example");
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Reply", Exact = true }).ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Open the post on mastodon.example" })).ToBeVisibleAsync();

        await probe.DrainAsync();
        var findings = CspViolationProbe.FormatFindings(probe.Violations, probe.ConsoleErrors);
        TestContext.Progress.WriteLine($"CSP measurement (discussion): {findings}");
        Assert.That(probe.Violations, Has.Count.EqualTo(0), findings);
        Assert.That(probe.ConsoleErrors, Has.Count.EqualTo(0), findings);
    }
}
