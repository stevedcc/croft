using Croft.Shared.Contracts;
using Croft.Shared.Enums;

namespace Croft.Web.E2E;

/// <summary>
/// Brief 07j item 1: the editor names what it edits. The browser title and the heading say
/// "New Post" / "Edit Post" for a post and "New Page" / "Edit Page" for a page — so writing the
/// About page is never addressed as a "Post". Each edit-route test creates its own post or page
/// through the API as the mock admin (the item under test is the editor's title, not creation —
/// PostCreationTests and PageWritingTests own those grounds). The blog name in the expected
/// document titles is branding.example/branding.json's "Field Notes", the E2E stack's branding.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class EditorTitleTests : WebTest
{
    [Test]
    public async Task NewPageEditor_SaysNewPage_InHeadingAndDocumentTitle()
    {
        await LoginAsync("admin@example.com", "password");

        await Page.GotoAsync($"{BaseUrl}/admin/pages/new");
        await WaitForInteractiveAsync(Page);

        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "New Page", Level = 1, Exact = true }))
            .ToBeVisibleAsync();
        Assert.That(await Page.TitleAsync(), Is.EqualTo("New Page · Field Notes"),
            "the browser title of the new-page route names the page kind");
    }

    [Test]
    public async Task NewPostEditor_SaysNewPost_InHeadingAndDocumentTitle()
    {
        await LoginAsync("admin@example.com", "password");

        await Page.GotoAsync($"{BaseUrl}/admin/posts/new");
        await WaitForInteractiveAsync(Page);

        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "New Post", Level = 1, Exact = true }))
            .ToBeVisibleAsync();
        Assert.That(await Page.TitleAsync(), Is.EqualTo("New Post · Field Notes"),
            "the browser title of the new-post route names the post kind");
    }

    [Test]
    public async Task EditRouteOfAPage_SaysEditPage_InHeadingAndDocumentTitle()
    {
        await LoginAsync("admin@example.com", "password");
        var id = await CreateAPostAsync(PostKind.Page);

        await Page.GotoAsync($"{BaseUrl}/admin/posts/{id}/edit");
        await WaitForInteractiveAsync(Page);

        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Edit Page", Level = 1, Exact = true }))
            .ToBeVisibleAsync();
        Assert.That(Page.Url, Is.EqualTo($"{BaseUrl}/admin/pages/{id}/edit"),
            "an old /admin/posts/{id}/edit bookmark of a page moves to the pages route");
        Assert.That(await Page.TitleAsync(), Is.EqualTo("Edit Page · Field Notes"),
            "the edit route of a page names the page kind, from the loaded post");
    }

    [Test]
    public async Task EditRouteOfAPost_SaysEditPost_InHeadingAndDocumentTitle()
    {
        await LoginAsync("admin@example.com", "password");
        var id = await CreateAPostAsync(PostKind.Post);

        await Page.GotoAsync($"{BaseUrl}/admin/posts/{id}/edit");
        await WaitForInteractiveAsync(Page);

        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Edit Post", Level = 1, Exact = true }))
            .ToBeVisibleAsync();
        Assert.That(await Page.TitleAsync(), Is.EqualTo("Edit Post · Field Notes"),
            "the edit route of a post names the post kind, from the loaded post");
    }

    /// <summary>Creates one draft of the given kind through the API as the mock admin and
    /// returns its id.</summary>
    private async Task<Guid> CreateAPostAsync(PostKind kind)
    {
        using var client = MockIdpApi.CreateClient();
        var token = await MockIdpApi.GetAdminAccessTokenAsync(client);
        var title = $"Titlecheck {Guid.NewGuid():N}";
        var post = await MockIdpApi.PostJsonAsync(client, ApiUrl, "/api/posts",
            new CreatePostRequest(title, "Body for the editor title check.", Kind: kind), token);
        return Guid.Parse(post.RootElement.GetProperty("id").GetString()!);
    }
}
