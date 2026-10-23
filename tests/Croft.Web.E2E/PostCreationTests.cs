namespace Croft.Web.E2E;

[Parallelizable(ParallelScope.Fixtures)]
public class PostCreationTests : WebTest
{
    [Test]
    public async Task AdminCan_CreateAndPublishPost()
    {
        await LoginAsync("admin@example.com", "password");

        // Route/selector migration (rule 5-sanctioned, disclosure in the commit message): the old
        // /admin editor is replaced by /admin/posts/new and "Publish / Schedule" by "Publish now".
        await OpenNewPostEditorAsync();

        var title = $"E2E Test Post {Guid.NewGuid():N}";

        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync(title);
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).BlurAsync();
        await Page.GetByLabel("Markdown").FillAsync("# E2E\n\nThis post was created by a Playwright test.");
        await Page.GetByLabel("Markdown").BlurAsync();

        var imagePath = Path.Combine(AppContext.BaseDirectory, "test-image.png");
        await Page.GetByLabel("Upload banner").SetInputFilesAsync(imagePath);
        await Expect(Page.GetByText("test-image.png").First).ToBeVisibleAsync();

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Publish now" }).ClickAsync();

        try
        {
            await Page.WaitForURLAsync(
                url => url.StartsWith($"{BaseUrl}/admin/posts/") && url.EndsWith("/edit"),
                new PageWaitForURLOptions { Timeout = 15000 });
        }
        catch (TimeoutException)
        {
            var url = Page.Url;
            var html = await Page.ContentAsync();
            throw new TimeoutException($"Publish did not navigate to the post editor. Current URL: {url}. Page content:\n{html}");
        }

        var actualSlug = await SlugForPostAsync(PostIdFromEditorUrl());

        // Verify the post is publicly visible
        await Page.GotoAsync($"{BaseUrl}/post/{actualSlug}");
        await Expect(Page.GetByText("This post was created by a Playwright test.")).ToBeVisibleAsync();

        // Ruling R2-R4 (same rationale) + Task R3.4's page rebuild: the shell adds brand images,
        // so the banner is selected within the hero — the old placeholder Alt="Banner" is gone
        // (the hero's alt is the media's own alt text). Locator input updated; assertions unchanged
        // — the banner image actually loads.
        // Outcome, not status: the <img> in the page decoded an image (complete, naturalWidth > 0) and
        // its URL serves image/png; a 200 HTML error page would pass a status check.
        await AssertImageLoadsAsync(Page.Locator(".post-hero img"));
    }
}
