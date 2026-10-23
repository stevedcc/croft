namespace Croft.Web.E2E;

[Parallelizable(ParallelScope.Fixtures)]
public class MediaProxyTests : WebTest
{
    [Test]
    public async Task AnonymousUser_CanViewPublishedBanner()
    {
        await LoginAsync("admin@example.com", "password");

        // Route/selector migration (rule 5-sanctioned): /admin → /admin/posts/new, the banner
        // upload is the panel's labelled input, "Publish / Schedule" → "Publish now".
        await OpenNewPostEditorAsync();

        var title = $"Published Banner {Guid.NewGuid():N}";
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync(title);
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).BlurAsync();
        await Page.GetByLabel("Markdown").FillAsync("Post with banner.");
        await Page.GetByLabel("Markdown").BlurAsync();

        var imagePath = Path.Combine(AppContext.BaseDirectory, "test-image.png");
        await Page.GetByLabel("Upload banner").SetInputFilesAsync(imagePath);
        await Expect(Page.GetByText("test-image.png").First).ToBeVisibleAsync();

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Publish now" }).ClickAsync();
        await Page.WaitForURLAsync(
            url => url.StartsWith($"{BaseUrl}/admin/posts/") && url.EndsWith("/edit"),
            new PageWaitForURLOptions { Timeout = 15000 });

        var actualSlug = await SlugForPostAsync(PostIdFromEditorUrl());

        await Page.GotoAsync($"{BaseUrl}/logout");
        await Expect(Page.GetByText("Log in")).ToBeVisibleAsync();

        await Page.GotoAsync($"{BaseUrl}/post/{actualSlug}");

        // Task R3.4's page rebuild removed the old placeholder Alt="Banner": the hero now renders
        // the banner through spec §7.3's BannerOrFallback, whose alt is the media's own alt text
        // (empty here — the upload has none). Test input (locator) updated to the rebuilt markup;
        // the assertion is unchanged — the banner image actually loads.
        await AssertImageLoadsAsync(Page.Locator(".post-hero img"));
    }

    [Test]
    public async Task DraftImage_AdminCanView_AnonymousGetsForbidden()
    {
        await LoginAsync("admin@example.com", "password");

        await OpenNewPostEditorAsync();

        var title = $"Draft Image {Guid.NewGuid():N}";
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync(title);
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).BlurAsync();
        await Page.GetByLabel("Markdown").FillAsync("Draft post.");
        await Page.GetByLabel("Markdown").BlurAsync();

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save draft" }).ClickAsync();
        await Page.WaitForURLAsync(
            url => url.StartsWith($"{BaseUrl}/admin/posts/") && url.EndsWith("/edit"),
            new PageWaitForURLOptions { Timeout = 15000 });

        // The banner upload on a saved post stores the image under the post (post-specific), so
        // it stays invisible to anonymous visitors — the outcome asserted below.
        var imagePath = Path.Combine(AppContext.BaseDirectory, "test-image.png");
        await Page.GetByLabel("Upload banner").SetInputFilesAsync(imagePath);
        await Expect(Page.GetByText("test-image.png").First).ToBeVisibleAsync();

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save draft" }).ClickAsync();

        // Ruling R2-R4 (same rationale): the shell adds brand images, so the banner preview is
        // selected by its alt text; the assertion is unchanged — admin can load it, anonymous gets 403.
        var bannerSrc = await Page.Locator("img[alt='test-image.png']").GetAttributeAsync("src");
        Assert.That(bannerSrc, Is.Not.Null.And.Not.Empty);

        var adminResponse = await Page.APIRequest.GetAsync($"{BaseUrl}{bannerSrc}");
        Assert.That(adminResponse.Status, Is.EqualTo(200));

        await Page.GotoAsync($"{BaseUrl}/logout");
        await Expect(Page.GetByText("Log in")).ToBeVisibleAsync();

        var anonymousResponse = await Page.APIRequest.GetAsync($"{BaseUrl}{bannerSrc}");
        Assert.That(anonymousResponse.Status, Is.EqualTo(403));
    }

    [Test]
    public async Task InlineImage_RendersOnPublicPost()
    {
        await LoginAsync("admin@example.com", "password");

        await OpenNewPostEditorAsync();

        var title = $"Inline Image {Guid.NewGuid():N}";
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync(title);
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).BlurAsync();
        await Page.GetByLabel("Markdown").FillAsync("Post with inline image.");
        await Page.GetByLabel("Markdown").BlurAsync();

        var imagePath = Path.Combine(AppContext.BaseDirectory, "test-image.png");
        await Page.GetByLabel("Upload banner").SetInputFilesAsync(imagePath);
        await Expect(Page.GetByText("test-image.png").First).ToBeVisibleAsync();

        // The markdown toolbar's "Insert image" uploads and inserts ![name](url) at the caret;
        // the old editor's "Inline:" line was only its upload-finished sync point — wait on the
        // concrete outcome instead (the markdown carries the image).
        await Page.GetByLabel("Upload inline image").SetInputFilesAsync(imagePath);
        await Page.WaitForFunctionAsync(
            "() => { const t = document.querySelector('.md-editor__textarea'); return t && t.value.includes('![test-image.png](/api/media/'); }");

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Publish now" }).ClickAsync();
        await Page.WaitForURLAsync(
            url => url.StartsWith($"{BaseUrl}/admin/posts/") && url.EndsWith("/edit"),
            new PageWaitForURLOptions { Timeout = 15000 });

        var actualSlug = await SlugForPostAsync(PostIdFromEditorUrl());

        await Page.GotoAsync($"{BaseUrl}/logout");
        await Expect(Page.GetByText("Log in")).ToBeVisibleAsync();

        await Page.GotoAsync($"{BaseUrl}/post/{actualSlug}");

        // Ruling R2-R4 (same rationale) + Task R3.4's page rebuild: the shell adds brand images,
        // so the banner is selected within the hero — the old placeholder Alt="Banner" is gone
        // (the hero's alt is the media's own alt text). Locator input updated; assertions unchanged.
        var banner = Page.Locator(".post-hero img");
        var inline = Page.Locator(".markdown-body img[alt='test-image.png']");

        await AssertImageLoadsAsync(banner);
        await AssertImageLoadsAsync(inline);

        var bannerSrc = await banner.GetAttributeAsync("src");
        var inlineSrc = await inline.GetAttributeAsync("src");
        Assert.That(bannerSrc, Is.Not.EqualTo(inlineSrc), "Banner and inline image should have different sources.");
    }
}
