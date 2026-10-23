using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Npgsql;

namespace Croft.Web.E2E;

/// <summary>
/// Brief 12d item 7: announcing on publish, in a real browser. The E2E instances keep 12b's settings (social on, no Mastodon server, no Bluesky DID, no
/// credentials), so neither network is ready: nothing is queued, nothing is sent, and the Discussion tab says so. The rows the display tests need are
/// seeded straight into the outbox table (<see cref="AnnouncementSeed"/>, invented values, `.example` hosts). No test calls Mastodon, Bluesky or any other real service.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
[Category("Announcing")]
public class AnnouncingTests : WebTest
{
    private static string NewRunId() => Guid.NewGuid().ToString("N")[..12];

    private ILocator Fields => Page.Locator(".post-form__announcement");

    private static ILocator Count(IPage page, string network) => page.Locator($"#announce-preview-{network} .announce-preview__count");

    private async Task FillTitleAndBodyAsync(string title, string body)
    {
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync(title);
        await Page.GetByLabel("Markdown").FillAsync(body);
    }

    [Test]
    public async Task ANewPost_HasBothFlagsOn_AndAPageHasNoAnnouncementFields()
    {
        await LoginAsync("admin@example.com", "password");

        await OpenNewPostEditorAsync();
        await Expect(Page.GetByLabel("Announce on Mastodon when published")).ToBeCheckedAsync();
        await Expect(Page.GetByLabel("Announce on Bluesky when published")).ToBeCheckedAsync();
        await Expect(Page.GetByLabel("Announcement text")).ToBeVisibleAsync();
        await Expect(Page.GetByText("Hashtags (#homelab) are linked on both networks.")).ToBeVisibleAsync();
        await Expect(Page.Locator(".announce-preview")).ToHaveCountAsync(2);

        await Page.GotoAsync($"{BaseUrl}/admin/pages/new");
        await WaitForInteractiveAsync(Page);
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "New Page" })).ToBeVisibleAsync();
        await Expect(Page.GetByLabel("Announce on Mastodon when published")).ToHaveCountAsync(0);
        await Expect(Page.GetByLabel("Announcement text")).ToHaveCountAsync(0);
        await Expect(Page.Locator(".announce-preview")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task Typing_TheText_UpdatesBothCounts_EachByItsOwnRule()
    {
        await LoginAsync("admin@example.com", "password");
        await OpenNewPostEditorAsync();
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync("Counts");
        var address = $"{BaseUrl}/post/counts";

        await Page.GetByLabel("Announcement text").FillAsync("Hello");

        // "Hello" + a blank line + the address: Bluesky counts the address as written, Mastodon as 23 characters.
        await Expect(Count(Page, "bluesky")).ToHaveTextAsync($"{5 + 2 + address.Length} / 300");
        await Expect(Count(Page, "mastodon")).ToHaveTextAsync($"{5 + 2 + 23} / 500");
        await Expect(Page.Locator("#announce-preview-bluesky .announce-preview__text")).ToHaveTextAsync($"Hello\n\n{address}");

        await Page.GetByLabel("Announcement text").FillAsync("Hello there");
        await Expect(Count(Page, "bluesky")).ToHaveTextAsync($"{11 + 2 + address.Length} / 300");
        await Expect(Count(Page, "mastodon")).ToHaveTextAsync($"{11 + 2 + 23} / 500");

        // The preview keeps its line break through a class: no inline style of ours.
        Assert.That(await Page.Locator(".announce-preview [style]").CountAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task ATextOverBlueskysLimit_IsRefusedUnderItsField_AndTheRestOfTheFormIsUnchanged()
    {
        var runId = NewRunId();
        await LoginAsync("admin@example.com", "password");
        await OpenNewPostEditorAsync();
        await FillTitleAndBodyAsync($"Too long {runId}", $"Body {runId}.");
        var text = Page.GetByLabel("Announcement text");
        await text.FillAsync(new string('x', 330));
        await Expect(Page.Locator("#announce-preview-bluesky .announce-preview__count--over")).ToBeVisibleAsync();
        await Expect(Page.Locator("#announce-preview-mastodon .announce-preview__count--over")).ToHaveCountAsync(0);

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save draft" }).ClickAsync();

        var message = Fields.Locator(".mud-input-helper-text");
        await Expect(message).ToContainTextAsync("AnnouncementText is too long for Bluesky (");
        await Expect(message).ToContainTextAsync("of 300 characters)");
        await Expect(text).ToHaveAttributeAsync("aria-invalid", "true");
        await Expect(Page.Locator(".post-edit__error")).ToHaveCountAsync(0);
        Assert.That(Page.Url, Does.EndWith("/admin/posts/new"), "a refused save does not leave the new-post editor");
        await Expect(Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true })).ToHaveValueAsync($"Too long {runId}");
        await Expect(text).ToHaveValueAsync(new string('x', 330));

        // Typing withdraws the message; a text that fits saves.
        await text.FillAsync("Short enough");
        await Expect(message).Not.ToContainTextAsync("AnnouncementText is too long");
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save draft" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex(@"/admin/posts/[0-9a-f-]{36}/edit$"));
        await Expect(Page.GetByLabel("Announcement text")).ToHaveValueAsync("Short enough");
    }

    [Test]
    public async Task PublishingAPost_OnASiteWithNoCredentials_QueuesNothing_AndTheDiscussionTabSaysNotSetUp()
    {
        var runId = NewRunId();
        await LoginAsync("admin@example.com", "password");
        await OpenNewPostEditorAsync();
        await FillTitleAndBodyAsync($"Published quietly {runId}", $"Body {runId}.");

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Publish now" }).ClickAsync();

        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex(@"/admin/posts/[0-9a-f-]{36}/edit$"));
        var postId = Guid.Parse(Page.Url.Split('/')[^2]);
        await Expect(Page.Locator(".post-actions__saved")).ToHaveTextAsync("Published", new LocatorAssertionsToHaveTextOptions { Timeout = 15000 });

        await Page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Discussion" }).ClickAsync();
        await Expect(Page.Locator("#announcement-mastodon .discussion-admin__announcement-text")).ToHaveTextAsync("Mastodon: Not set up on this site");
        await Expect(Page.Locator("#announcement-bluesky .discussion-admin__announcement-text")).ToHaveTextAsync("Bluesky: Not set up on this site");
        await Expect(Page.Locator(".discussion-admin__announcements button")).ToHaveCountAsync(0);

        await using var db = await OpenInstanceDatabaseAsync();
        Assert.That(await AnnouncementSeed.CountAsync(db, postId), Is.EqualTo(0), "neither network is ready, so nothing is queued");
    }

    [Test]
    public async Task TheAnnouncementsBlock_ShowsEachState_WithItsLinkAndNoButtonsOnASiteThatCannotAnnounce()
    {
        var runId = NewRunId();
        var (id, _) = await DiscussionSeed.PublishPostAsync(ApiUrl, $"Announced {runId}", $"Body {runId}.");
        await using (var db = await OpenInstanceDatabaseAsync())
        {
            await AnnouncementSeed.AddAsync(db, id, runId);
        }

        await LoginAsync("admin@example.com", "password");
        await Page.GotoAsync($"{BaseUrl}/admin/posts/{id}/edit");
        await WaitForInteractiveAsync(Page);
        await Page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Discussion" }).ClickAsync();

        await Expect(Page.Locator("#announcement-mastodon .discussion-admin__announcement-text")).ToContainTextAsync("Mastodon: Posted ");
        var link = Page.Locator("#announcement-mastodon a");
        await Expect(link).ToHaveAttributeAsync("href", $"https://social.example/@owner/{runId}");
        await Expect(link).ToHaveTextAsync("View on Mastodon");
        await Expect(Page.Locator("#announcement-bluesky .discussion-admin__announcement-text"))
            .ToHaveTextAsync("Bluesky: Failed: Bluesky refused the post (HTTP 400): InvalidRequest");
    }

    // ---- hashtags of the category (brief 12e) ----

    /// <summary>The length of a preview's text by the network's own rule: Mastodon counts every address as 23, Bluesky as written; both count graphemes.</summary>
    private static int LengthByRule(string network, string text)
    {
        var length = new System.Globalization.StringInfo(text).LengthInTextElements;
        if (network != "mastodon")
            return length;

        var address = System.Text.RegularExpressions.Regex.Match(text, @"https?://\S+");
        return address.Success ? length - new System.Globalization.StringInfo(address.Value).LengthInTextElements + 23 : length;
    }

    [Test]
    public async Task ACategorysHashtags_AppearInEachPreviewByNetwork_AndGoWhenNoCategoryIsPicked()
    {
        var runId = NewRunId();
        var categoryName = $"E2E Tags {runId}";
        using (var client = MockIdpApi.CreateClient())
        {
            var token = await MockIdpApi.GetAdminAccessTokenAsync(client);
            using var created = await MockIdpApi.PostJsonAsync(client, ApiUrl, "/api/categories", System.Text.Json.JsonSerializer.Serialize(categoryName), token);
            var id = created.RootElement.GetProperty("id").GetGuid();
            using var update = new HttpRequestMessage(HttpMethod.Put, $"{ApiUrl}/api/categories/{id}")
            {
                Content = new StringContent(
                    $$"""{"name":"{{categoryName}}","hashtags":[{"name":"Homelab","networks":0},{"name":"SelfHosted","networks":2}]}""",
                    System.Text.Encoding.UTF8, "application/json"),
            };
            update.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(update);
            Assert.That((int)response.StatusCode, Is.EqualTo(200), "the category took its hashtags");
        }

        await LoginAsync("admin@example.com", "password");
        await OpenNewPostEditorAsync();
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync("Hashtag preview");
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).BlurAsync();
        var address = $"{BaseUrl}/post/hashtag-preview";
        var mastodon = Page.Locator("#announce-preview-mastodon .announce-preview__text");
        var bluesky = Page.Locator("#announce-preview-bluesky .announce-preview__text");
        await Expect(mastodon).ToHaveTextAsync($"Hashtag preview\n\n{address}");

        var category = Page.GetByRole(AriaRole.Combobox, new PageGetByRoleOptions { Name = "Category" });
        await category.ClickAsync();
        await category.FillAsync(categoryName);
        await Page.GetByRole(AriaRole.Option, new PageGetByRoleOptions { Name = categoryName }).ClickAsync();

        await Expect(mastodon).ToHaveTextAsync($"Hashtag preview\n\n#Homelab\n\n{address}");
        await Expect(bluesky).ToHaveTextAsync($"Hashtag preview\n\n#Homelab #SelfHosted\n\n{address}");
        foreach (var network in new[] { "mastodon", "bluesky" })
        {
            var shown = (await Page.Locator($"#announce-preview-{network} .announce-preview__text").TextContentAsync())!;
            var limit = network == "mastodon" ? 500 : 300;
            await Expect(Count(Page, network)).ToHaveTextAsync($"{LengthByRule(network, shown)} / {limit}");
        }

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Clear" }).ClickAsync();

        await Expect(mastodon).ToHaveTextAsync($"Hashtag preview\n\n{address}");
        await Expect(bluesky).ToHaveTextAsync($"Hashtag preview\n\n{address}");
    }

    [Test]
    public async Task APendingAnnouncement_BecomesPosted_WithoutReloadingTheTab()
    {
        var runId = NewRunId();
        var (id, _) = await DiscussionSeed.PublishPostAsync(ApiUrl, $"Pending then posted {runId}", $"Body {runId}.");
        await using var db = await OpenInstanceDatabaseAsync();
        await AnnouncementSeed.AddPendingAsync(db, id);

        await LoginAsync("admin@example.com", "password");
        await Page.GotoAsync($"{BaseUrl}/admin/posts/{id}/edit");
        await WaitForInteractiveAsync(Page);
        await Page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Discussion" }).ClickAsync();
        await Expect(Page.Locator("#announcement-mastodon .discussion-admin__announcement-text")).ToContainTextAsync("Mastodon: Pending");

        await AnnouncementSeed.MarkPostedAsync(db, id, $"https://social.example/@owner/{runId}");

        // The sender posted behind the page's back; the tab notices on its own, within a few polls.
        var slow = new LocatorAssertionsToHaveAttributeOptions { Timeout = 15000 };
        await Expect(Page.Locator("#announcement-mastodon a")).ToHaveAttributeAsync("href", $"https://social.example/@owner/{runId}", slow);
        await Expect(Page.Locator("#announcement-mastodon .discussion-admin__announcement-text")).ToContainTextAsync("Mastodon: Posted ");
    }

    // ---- accessibility ----

    private static async Task AssertNoSeriousAxeViolationsAsync(IPage page)
    {
        AxeResult axe = await page.RunAxe();
        var serious = axe.Violations.Where(v => v.Impact is "serious" or "critical").ToList();
        var report = string.Join("\n\n", serious.Select(v =>
            $"{v.Id} ({v.Impact}): {v.Help}\n{v.HelpUrl}\n" + string.Join("\n", v.Nodes.Select(n => $"  {n.Target} :: {n.Html}"))));
        Assert.That(serious, Is.Empty, $"serious/critical axe violations on {page.Url}:\n{report}");
    }

    [TestCase("fields", false)]
    [TestCase("fields", true)]
    [TestCase("tab", false)]
    [TestCase("tab", true)]
    public async Task TheAnnouncementSurfaces_HaveNoSeriousOrCriticalAxeViolations(string surface, bool dark)
    {
        var runId = NewRunId();
        var (id, _) = await DiscussionSeed.PublishPostAsync(ApiUrl, $"Axe announcing {runId}", $"Body {runId}.");
        await using (var db = await OpenInstanceDatabaseAsync())
        {
            await AnnouncementSeed.AddAsync(db, id, runId);
        }

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
        await LoginAsync(page, "admin@example.com", "password");
        await page.GotoAsync($"{BaseUrl}/admin/posts/{id}/edit");
        await WaitForInteractiveAsync(page);
        if (surface == "fields")
        {
            await page.GetByLabel("Announcement text").FillAsync(new string('y', 330));
            await Expect(page.Locator(".announce-preview__count--over")).ToBeVisibleAsync();
        }
        else
        {
            await page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Discussion" }).ClickAsync();
            await Expect(page.Locator("#announcement-mastodon a")).ToBeVisibleAsync();
        }

        if (dark)
        {
            await WaitForDarkPaletteAsync(page);
        }

        await AssertNoSeriousAxeViolationsAsync(page);
    }
}

/// <summary>Announcement rows put straight into the outbox table (the display tests need states the stub-free instances never produce). Invented values.</summary>
internal static class AnnouncementSeed
{
    private const int Mastodon = 1;
    private const int Bluesky = 2;

    /// <summary>A posted Mastodon row (with its link) and a failed Bluesky row (with its error).</summary>
    public static async Task AddAsync(NpgsqlConnection db, Guid postId, string runId)
    {
        await using var posted = db.CreateCommand();
        posted.CommandText = """
            INSERT INTO "SocialAnnouncements" ("Id", "PostId", "Platform", "State", "Attempts", "QueuedAt", "LastAttemptAt", "RemoteId", "RemoteUrl", "PostedAt", "SentText")
            VALUES (@id, @post, @platform, 2, 1, now() - interval '2 hours', now() - interval '2 hours', @remote, @url, now() - interval '2 hours', 'Seeded text')
            """;
        posted.Parameters.AddWithValue("id", Guid.CreateVersion7());
        posted.Parameters.AddWithValue("post", postId);
        posted.Parameters.AddWithValue("platform", Mastodon);
        posted.Parameters.AddWithValue("remote", runId);
        posted.Parameters.AddWithValue("url", $"https://social.example/@owner/{runId}");
        await posted.ExecuteNonQueryAsync();

        await using var failed = db.CreateCommand();
        failed.CommandText = """
            INSERT INTO "SocialAnnouncements" ("Id", "PostId", "Platform", "State", "Attempts", "QueuedAt", "LastAttemptAt", "LastError")
            VALUES (@id, @post, @platform, 3, 1, now() - interval '2 hours', now() - interval '2 hours', 'Bluesky refused the post (HTTP 400): InvalidRequest')
            """;
        failed.Parameters.AddWithValue("id", Guid.CreateVersion7());
        failed.Parameters.AddWithValue("post", postId);
        failed.Parameters.AddWithValue("platform", Bluesky);
        await failed.ExecuteNonQueryAsync();
    }

    /// <summary>A Pending Mastodon row, due now, as the publish queues it.</summary>
    public static async Task AddPendingAsync(NpgsqlConnection db, Guid postId)
    {
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO "SocialAnnouncements" ("Id", "PostId", "Platform", "State", "Attempts", "QueuedAt", "NextAttemptAt")
            VALUES (@id, @post, @platform, 1, 0, now(), now() + interval '1 hour')
            """;
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("post", postId);
        command.Parameters.AddWithValue("platform", Mastodon);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>What the sender writes when it has posted: the row is Posted, with its link.</summary>
    public static async Task MarkPostedAsync(NpgsqlConnection db, Guid postId, string url)
    {
        await using var command = db.CreateCommand();
        command.CommandText = """
            UPDATE "SocialAnnouncements" SET "State" = 2, "Attempts" = 1, "NextAttemptAt" = NULL, "LastAttemptAt" = now(),
                "PostedAt" = now(), "RemoteId" = 'seeded', "RemoteUrl" = @url, "SentText" = 'Seeded text'
            WHERE "PostId" = @post
            """;
        command.Parameters.AddWithValue("post", postId);
        command.Parameters.AddWithValue("url", url);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<long> CountAsync(NpgsqlConnection db, Guid postId)
    {
        await using var command = db.CreateCommand();
        command.CommandText = """SELECT count(*) FROM "SocialAnnouncements" WHERE "PostId" = @post""";
        command.Parameters.AddWithValue("post", postId);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
