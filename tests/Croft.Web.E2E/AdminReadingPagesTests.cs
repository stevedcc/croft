using System.Text.RegularExpressions;
using Croft.Shared.Enums;

namespace Croft.Web.E2E;

/// <summary>
/// Brief 10b item 8: the reading pages show an admin what visitors see. A published page (Kind =
/// Page) must not be the Featured card or any card of the home list for a signed-in admin, while
/// the admin Pages list (since brief 10f), which is the admin variant, still lists it.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class AdminReadingPagesTests : StaticReadingTest
{
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];

    [Test]
    public async Task SignedInAdmin_HomeListsPostsOnly_AndTheAdminPagesListHasThePage()
    {
        var pageTitle = $"Admin view page {RunId}";
        var postTitle = $"Admin view post {RunId}";
        await PublishAsync(postTitle, BodyWithSections(RunId, 1));
        await PublishAsync(pageTitle, BodyWithSections(RunId, 1), kind: PostKind.Page);

        await LoginAsync("admin@example.com", "password");
        await Page.GotoAsync(BaseUrl);

        await Expect(Page.Locator(".post-card--featured")).ToContainTextAsync(postTitle);
        await Expect(Page.Locator(".post-card", new PageLocatorOptions { HasText = pageTitle })).ToHaveCountAsync(0);

        // Brief 10f moved pages out of Admin -> Posts into Admin -> Pages.
        await Page.GotoAsync($"{BaseUrl}/admin/pages");
        await Expect(Page.GetByText(new Regex(Regex.Escape(pageTitle))).First).ToBeVisibleAsync();
    }
}
