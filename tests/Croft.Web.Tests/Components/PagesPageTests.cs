using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Croft.Web.Components.Pages.Admin;
using Croft.Web.Services;
using Bunit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace Croft.Web.Tests.Components;

/// <summary>Brief 10f item 3: the admin Pages list — the posts grid filtered to pages, with the
/// placement and order columns and the edit and view actions (pages have no analytics).</summary>
public class PagesPageTests : BlogBunitContext
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private FakeBlogApiClient _api = null!;

    private void Setup(params PostSummaryDto[] posts)
    {
        _api = new FakeBlogApiClient { Posts = [.. posts] };
        Services.AddSingleton<IBlogApiClient>(_api);
    }

    private static PostSummaryDto Summary(string slug, string title, PostKind kind, NavigationPlacement placement = NavigationPlacement.None,
        int order = 0, PostStatus status = PostStatus.Published) =>
        new(Guid.CreateVersion7(), slug, title, null, null, "A summary.", 2, status,
            status == PostStatus.Draft ? null : Now.UtcDateTime, new DateTime(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc),
            "An Author", null, [], null, null, null, kind, placement, order);

    [Fact]
    public async Task PagesPage_ListsPagesOnly_WithPlacementOrderAndUpdated()
    {
        Setup(
            Summary("impressum", "Impressum", PostKind.Page, NavigationPlacement.Footer, 2),
            Summary("about", "About", PostKind.Page, NavigationPlacement.Sidebar, 1),
            Summary("hidden", "Hidden", PostKind.Page, status: PostStatus.Draft),
            Summary("a-post", "A Post", PostKind.Post));
        var cut = Render<PagesPage>();

        await cut.WaitForAssertionAsync(() => cut.FindAll(".posts-grid__row").Should().HaveCount(3,
            "three pages, and the post is not listed"));

        _api.PostRequests.Should().OnlyContain(r => r.Kind == PostKind.Page, "the pages list asks for Kind == Page");
        cut.FindAll("th").Select(h => h.TextContent.Trim()).Where(t => t.Length > 0)
            .Should().Equal("Title", "Status", "Show in", "Order", "Updated", "Actions");
        var rows = cut.FindAll(".posts-grid__row").ToDictionary(
            r => r.QuerySelector(".posts-grid__title")!.TextContent,
            r => (Placement: r.QuerySelector(".posts-grid__placement")!.TextContent,
                  Order: r.QuerySelector(".posts-grid__order")!.TextContent,
                  Updated: r.QuerySelector(".posts-grid__updated")!.TextContent));
        rows["Impressum"].Should().Be(("Footer", "2", "5 October 2026"));
        rows["About"].Should().Be(("Sidebar", "1", "5 October 2026"));
        rows["Hidden"].Placement.Should().Be("–", "a page shown nowhere reads as a dash");
        cut.FindAll(".posts-grid__kind, .posts-grid__views, .posts-grid__feedback, .posts-grid__category")
            .Should().BeEmpty("the page columns replace the post columns");
    }

    [Fact]
    public async Task PagesPage_RowActions_AreEditAndViewOnly_AndEditOpensTheEditor()
    {
        var page = Summary("about", "About", PostKind.Page, NavigationPlacement.Sidebar, 1);
        Setup(page);
        var navigation = Services.GetRequiredService<NavigationManager>();
        var cut = Render<PagesPage>();
        await cut.WaitForAssertionAsync(() => cut.FindAll(".posts-grid__row").Should().ContainSingle());

        var actions = cut.Find(".posts-grid__row").QuerySelectorAll(".posts-grid__actions a, .posts-grid__actions button");
        actions.Select(a => a.TextContent).Should().Equal("Edit", "View", "Delete");
        cut.Find("a.posts-grid__title").GetAttribute("href").Should().Be($"/admin/pages/{page.Id}/edit",
            "the title links to the pages edit route");
        actions.Single(a => a.TextContent == "View").GetAttribute("href").Should().Be("/about");

        actions.Single(a => a.TextContent == "Edit").Click(new MouseEventArgs());
        navigation.Uri.Should().EndWith($"/admin/pages/{page.Id}/edit", "a page opens on the pages edit route");
    }

    [Fact]
    public async Task PagesPage_Delete_AsksAboutThePage_WithoutTheFeedbackClause()
    {
        Setup(Summary("about", "About", PostKind.Page, NavigationPlacement.Sidebar, 1));
        var dialogs = Render<MudDialogProvider>();
        var cut = Render<PagesPage>();
        await cut.WaitForAssertionAsync(() => cut.FindAll(".posts-grid__row").Should().ContainSingle());

        cut.Find(".posts-grid__delete").Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => dialogs.Markup.Should().Contain("Delete page?"));
        dialogs.Markup.Should().Contain("\"About\" is deleted for good.")
            .And.NotContain("feedback", "pages have no feedback");
        dialogs.Markup.Should().NotContain("Delete post?");
    }

    [Fact]
    public async Task PagesPage_EmptyList_SaysNoPagesFound()
    {
        Setup(Summary("a-post", "A Post", PostKind.Post));
        var cut = Render<PagesPage>();

        await cut.WaitForAssertionAsync(() =>
            cut.Find(".posts-grid__empty").TextContent.Should().Be("No pages found."));
    }
}
