namespace Croft.Web.E2E;

/// <summary>
/// Brief 11a item 4: the owner's profile links (<c>rel="me"</c>) and the
/// <c>fediverse:creator</c> meta render in the head of every page — a static reading page and
/// an interactive admin page both carry them, from the document shell. The E2E stack runs on
/// branding.example, whose branding.json supplies four example profiles and a fediverse
/// handle; the assertions pin that example in owner order.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class ProfileLinksTests : WebTest
{
    private static readonly string[] ExampleProfileUrls =
    [
        "https://example.social/@someone",
        "https://bsky.app/profile/example.com",
        "https://github.com/someone",
        "https://www.linkedin.com/in/someone",
    ];

    [Test]
    public async Task ReadingPage_CarriesTheProfileLinksInOwnerOrder()
    {
        await Page.GotoAsync($"{BaseUrl}/");

        var hrefs = await ProfileHrefsAsync();
        Assert.That(hrefs, Is.EqualTo(ExampleProfileUrls),
            "the home page's head carries one rel=me link per profile, in the owner's order");
        Assert.That(await CreatorHandlesAsync(), Is.EqualTo(new[] { "@someone@example.social" }),
            "the home page's head carries the branding's fediverse:creator handle, exactly once");
    }

    [Test]
    public async Task AdminPage_CarriesTheProfileLinksToo()
    {
        await LoginAsync("admin@example.com", "password");
        await Page.GotoAsync($"{BaseUrl}/admin/posts");
        // The document is read once its circuit is up: the claim is that the head holds exactly one set
        // of links AFTER the interactive render (a HeadOutlet duplicate would only appear then).
        await WaitForInteractiveAsync(Page);

        var hrefs = await ProfileHrefsAsync();
        Assert.That(hrefs, Is.EqualTo(ExampleProfileUrls),
            "an interactive admin page carries the same links, from the same document shell");
        Assert.That(await CreatorHandlesAsync(), Is.EqualTo(new[] { "@someone@example.social" }),
            "exactly one fediverse:creator meta, even after the interactive render");
    }

    private async Task<string[]> ProfileHrefsAsync() =>
        await Page.Locator("head link[rel='me']")
            .EvaluateAllAsync<string[]>("els => els.map(el => el.getAttribute('href'))");

    private async Task<string[]> CreatorHandlesAsync() =>
        await Page.Locator("head meta[name='fediverse:creator']")
            .EvaluateAllAsync<string[]>("els => els.map(el => el.getAttribute('content'))");
}
