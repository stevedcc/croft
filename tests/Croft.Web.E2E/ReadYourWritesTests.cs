namespace Croft.Web.E2E;

/// <summary>
/// Brief 09d item 3: read-your-writes in the editor. With the E2E API configured with
/// Database:ReadHost pointing at the same database, a person editing a post saves a change
/// to its body, the editor reloads and the new body is shown (authored reads use the
/// primary), and the public post page shows it too (a refill right after a write reads the primary on
/// purpose, so the read host is proven by a separate anonymous read). The editing user must be the admin: editors can only edit
/// drafts, and the public page only exists for a published post.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class ReadYourWritesTests : WebTest
{
    private const string OriginalBody = "Body before the editor's change.";
    private const string ChangedBody = "Body after the editor's change must survive the reload.";

    [Test]
    public async Task EditorSavesBodyChange_ReloadShowsIt_AndThePublicPostPageShowsItToo()
    {
        await LoginAsync("admin@example.com", "password");
        var title = $"Read Your Writes {Guid.NewGuid():N}";
        await PublishPostAsync(title, OriginalBody);
        var postId = PostIdFromEditorUrl();
        var slug = await SlugForPostAsync(postId);

        // The editor saves a change to the post's body. Blazor Server runs the API call on the
        // server, so the browser-visible save signal is the form's data-draft-stamp attribute,
        // which the save refreshes with the post's new ContentEditedAt.
        var form = Page.Locator("[data-draft-stamp]");
        await Expect(form).ToBeVisibleAsync();
        var stampBefore = await form.GetAttributeAsync("data-draft-stamp");
        await Page.GetByLabel("Markdown").FillAsync(ChangedBody);
        await Page.GetByLabel("Markdown").BlurAsync();
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save draft" }).ClickAsync();
        await Expect(form).Not.ToHaveAttributeAsync("data-draft-stamp", stampBefore!,
            new LocatorAssertionsToHaveAttributeOptions { Timeout = 15000 });

        // The editor reloads: the new body is shown.
        await Page.ReloadAsync();
        await WaitForInteractiveAsync(Page);
        await Expect(Page.GetByLabel("Markdown")).ToHaveValueAsync(ChangedBody);

        // The public post page — an anonymous read — shows it too.
        await LogoutAsync();
        await Page.GotoAsync($"{BaseUrl}/post/{slug}");
        await Expect(Page.GetByText(ChangedBody, new PageGetByTextOptions { Exact = true })).ToBeVisibleAsync();

        // (a) The read above is a refill of a group the save invalidated a moment ago, so it is served from the
        // PRIMARY on purpose (Croft:Api:Cache:PrimaryAfterInvalidation, brief 13c): that is why it shows the
        // new body at once, and why it cannot prove anything about the read host.

        // (b) The premise of the class comment, from the instance itself: it was started with
        // Database:ReadHost (start-local.sh --e2e),
        var apiLog = ApiLogText();
        Assert.That(apiLog, Does.Contain("Public reads route to read host"),
            "the API instance was started with Database:ReadHost");
        Assert.That(apiLog, Does.Not.Contain("Public reads use the primary connection"),
            "the instance did not fall back to the primary for public reads");

        // ... and a separate anonymous read goes over it. The read must not be a cache refill inside the
        // primary window, which would route to the primary whenever this runs within a minute of a write
        // (always, in a short run): an anonymous search past the pages the API caches (page 4; brief 14b caches pages 1 to 3, PostService.MaxCachedSearchPage) is never cached, so it is read with the normal routing. The server's own record is taken from a clock read immediately
        // before: a read connection that did work after it served this read, whatever earlier fixtures did.
        var before = await DatabaseClockAsync();
        await using (var deep = await Page.APIRequest.GetAsync($"{ApiUrl}/api/search?q=readhostprobe&page=4"))
        {
            Assert.That(deep.Status, Is.EqualTo(200), "the anonymous deep search answers");
        }

        Assert.That(await ReadConnectionServedSinceAsync(before), Is.True,
            "an anonymous read that is not a refill went over the read host (a croft-api-read connection did work after it)");
    }

    private async Task PublishPostAsync(string title, string body)
    {
        await OpenNewPostEditorAsync();
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync(title);
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).BlurAsync();
        await Page.GetByLabel("Markdown").FillAsync(body);
        await Page.GetByLabel("Markdown").BlurAsync();

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Publish now" }).ClickAsync();
        await Page.WaitForURLAsync(
            url => url.StartsWith($"{BaseUrl}/admin/posts/") && url.EndsWith("/edit"),
            new PageWaitForURLOptions { Timeout = 15000 });
    }

    private async Task LogoutAsync()
    {
        await SignOutAsync();
    }
}
