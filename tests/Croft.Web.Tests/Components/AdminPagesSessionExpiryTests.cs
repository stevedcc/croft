using Bunit;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Croft.Web.Components.Admin;
using Croft.Web.Components.Pages.Admin;
using Croft.Web.Components.Pages.Editor;
using Croft.Web.Services;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests.Components;

/// <summary>
/// The admin and editor pages besides the post editor: a session that ends while one of them
/// loads or acts is shown as the page's own error line while the sign-in redirect runs. The
/// exception never ends the circuit (an unhandled one used to leave a dead shell).
/// </summary>
public class AdminPagesSessionExpiryTests : BlogBunitContext
{
    private const string Expected = "The session has expired; sign in again.";

    [Fact]
    public async Task TagsPage_SessionEndingWhileItLoads_ShowsTheErrorLine()
    {
        var fake = new FakeBlogApiClient { CategoriesFailure = new SessionExpiredException() };
        Services.AddSingleton<IBlogApiClient>(fake);

        var cut = Render<TagsPage>();

        await cut.WaitForAssertionAsync(() => cut.Find(".admin-table__error").TextContent.Should().Contain(Expected));
    }

    [Fact]
    public void MediaGrid_SessionEndingOnDelete_ShowsTheErrorLine_AndKeepsTheTile()
    {
        var asset = new MediaAssetDto(Guid.CreateVersion7(), "f.png", "photo.png", "image/png", "/api/media/x",
            MediaAssetScope.Global, null, null);
        var fake = new FakeBlogApiClient { DeleteFailure = new SessionExpiredException(), GlobalMedia = [asset] };
        Services.AddSingleton<IBlogApiClient>(fake);
        var cut = Render<MediaGrid>();

        cut.FindAll(".media-tile__actions button").Single(b => b.TextContent == "Delete").Click(new MouseEventArgs());
        cut.FindAll(".media-tile__confirm button").Single(b => b.TextContent == "Delete image").Click(new MouseEventArgs());

        cut.Find(".media-grid__error").TextContent.Should().Contain(Expected);
        cut.FindAll(".media-tile").Should().HaveCount(1);
    }

    [Fact]
    public async Task DraftReviewPage_SessionEndingWhileItLoads_ShowsTheErrorLine()
    {
        var fake = new FakeBlogApiClient { PostReadFailure = new SessionExpiredException() };
        Services.AddSingleton<IBlogApiClient>(fake);

        var cut = Render<DraftReviewPage>(p => p.Add(x => x.Slug, "the-draft"));

        await cut.WaitForAssertionAsync(() => cut.Find(".draft-review__error").TextContent.Should().Contain(Expected));
    }
}
