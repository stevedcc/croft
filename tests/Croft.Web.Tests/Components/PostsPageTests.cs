using AngleSharp.Dom;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Croft.Web.Components.Admin;
using Croft.Web.Components.Pages.Admin;
using Croft.Web.Services;
using Bunit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace Croft.Web.Tests.Components;

/// <summary>
/// The admin posts list (spec §6.1): /admin lands here, the status chips and the title search map
/// to <c>PostListRequest.Status</c>/<c>SearchTerm</c>, the row actions navigate to the editor,
/// the analytics page and the editor's Feedback tab, and Delete asks for confirmation before the
/// post leaves the list.
/// </summary>
public class PostsPageTests : BlogBunitContext
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private FakeBlogApiClient _api = null!;

    private FakeBlogApiClient Setup(params PostSummaryDto[] posts)
    {
        _api = new FakeBlogApiClient { Posts = [.. posts] };
        Services.AddSingleton<IBlogApiClient>(_api);
        return _api;
    }

    private static PostSummaryDto Summary(string slug, string title, PostStatus status, PostKind kind = PostKind.Post) =>
        new(Guid.CreateVersion7(), slug, title, null, null, "A summary.", 2, status,
            status == PostStatus.Draft ? null : Now.UtcDateTime, Now.UtcDateTime, "An Author", null, [], null, 4, 1, kind);

    [Fact]
    public async Task PostsPage_ListsPostsOnly_WithNoKindFilter()
    {
        // Brief 10f item 3: pages have their own list (/admin/pages), so Posts asks for posts only
        // and no longer offers the kind chips.
        Setup(Summary("the-post", "The Post", PostStatus.Published),
            Summary("the-page", "The Page", PostStatus.Published, PostKind.Page));
        var cut = Render<PostsPage>();

        await cut.WaitForAssertionAsync(() =>
        {
            _api.PostRequests.Should().OnlyContain(r => r.Kind == PostKind.Post, "the posts list asks for Kind == Post");
            cut.FindAll(".posts-grid__row").Should().ContainSingle("the page is not listed under Posts")
                .Which.TextContent.Should().Contain("The Post");
        });
        cut.FindAll("button").Select(b => b.TextContent).Should().NotContain(["Both kinds", "Posts", "Pages"],
            "the kind chips are gone");
        cut.FindAll("[aria-label='Filter by kind']").Should().BeEmpty();
        cut.FindAll(".posts-grid__kind").Should().BeEmpty("every row is a post, so the Kind column is gone");
    }

    private static IElement ActionFor(IRenderedComponent<PostsPage> cut, string name) =>
        cut.FindAll(".posts-grid__row").Single().QuerySelectorAll("a, button")
            .Single(element => element.TextContent == name);

    [Fact]
    public void PostsPage_BareAdmin_RedirectsToThePostsList()
    {
        Setup();
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/admin");

        Render<PostsPage>();

        navigation.Uri.Should().EndWith("/admin/posts", "spec §6.1: /admin redirects to the posts list");
    }

    [Fact]
    public void PostsPage_BareAdminRedirect_PreservesTheQueryString()
    {
        Setup();
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/admin?page=2");

        Render<PostsPage>();

        navigation.Uri.Should().EndWith("/admin/posts?page=2",
            "the redirect must carry any query string along, not drop it");
    }

    [Fact]
    public void PostsPage_SearchButton_HasItsOwnAccessibleName()
    {
        Setup();
        var cut = Render<PostsPage>();

        // The adornment button must not repeat the textbox's "Search titles" label: two controls
        // with the same accessible name make name-based locators ambiguous. It names its action
        // instead, like Search.razor's button.
        cut.Find(".posts-search .mud-input-adornment button").GetAttribute("aria-label")
            .Should().Be("Search");
    }

    [Fact]
    public async Task PostsPage_FilterChips_MapToStatus_AndChangeTheRows()
    {
        Setup(Summary("the-post", "The Published", PostStatus.Published),
            Summary("the-draft", "The Draft", PostStatus.Draft));
        var cut = Render<PostsPage>();
        await cut.WaitForAssertionAsync(() => cut.FindAll(".posts-grid__row").Should().HaveCount(2));

        cut.FindAll("button").Single(b => b.TextContent == "Draft").Click(new MouseEventArgs());
        await cut.WaitForAssertionAsync(() =>
        {
            _api.PostRequests.Last().Status.Should().Be(PostStatus.Draft, "the chip maps to PostListRequest.Status");
            var rows = cut.FindAll(".posts-grid__row");
            rows.Should().ContainSingle("only the draft matches the filter")
                .Which.TextContent.Should().Contain("The Draft");
        });

        cut.FindAll("button").Single(b => b.TextContent == "Scheduled").Click(new MouseEventArgs());
        await cut.WaitForAssertionAsync(() =>
        {
            _api.PostRequests.Last().Status.Should().Be(PostStatus.Scheduled);
            cut.FindAll(".posts-grid__row").Should().BeEmpty("nothing is scheduled");
            cut.Find(".posts-grid__empty").TextContent.Should().Be("No posts found.");
        });

        cut.FindAll("button").Single(b => b.TextContent == "Published").Click(new MouseEventArgs());
        await cut.WaitForAssertionAsync(() =>
        {
            _api.PostRequests.Last().Status.Should().Be(PostStatus.Published);
            cut.FindAll(".posts-grid__row").Should().ContainSingle()
                .Which.TextContent.Should().Contain("The Published");
        });

        cut.FindAll("button").Single(b => b.TextContent == "All").Click(new MouseEventArgs());
        await cut.WaitForAssertionAsync(() =>
        {
            _api.PostRequests.Last().Status.Should().BeNull("All clears the status filter");
            cut.FindAll(".posts-grid__row").Should().HaveCount(2);
        });
    }

    [Fact]
    public async Task PostsPage_TitleSearch_MapsToSearchTerm_AndChangesTheRows()
    {
        Setup(Summary("nimbus", "Nimbus Notes", PostStatus.Published),
            Summary("zephyr", "Zephyr Notes", PostStatus.Published));
        var cut = Render<PostsPage>();
        await cut.WaitForAssertionAsync(() => cut.FindAll(".posts-grid__row").Should().HaveCount(2));

        var field = cut.FindComponents<MudTextField<string>>().Single(f => f.Instance.Label == "Search titles");
        await cut.InvokeAsync(() => field.Instance.ValueChanged.InvokeAsync("Zephyr"));
        cut.Find("input").KeyDown(new KeyboardEventArgs { Key = "Enter" });

        await cut.WaitForAssertionAsync(() =>
        {
            _api.PostRequests.Last().SearchTerm.Should().Be("Zephyr", "the box maps to PostListRequest.SearchTerm");
            var rows = cut.FindAll(".posts-grid__row");
            rows.Should().ContainSingle("only the matching title is listed")
                .Which.TextContent.Should().Contain("Zephyr Notes");
        });
    }

    [Fact]
    public async Task PostsPage_TypingAndPressingEnter_SearchesWithTheTypedText()
    {
        // The user types and presses Enter without leaving the field: the reload must search with
        // what is typed, not with a stale value.
        Setup(Summary("nimbus", "Nimbus Notes", PostStatus.Published),
            Summary("zephyr", "Zephyr Notes", PostStatus.Published));
        var cut = Render<PostsPage>();
        await cut.WaitForAssertionAsync(() => cut.FindAll(".posts-grid__row").Should().HaveCount(2));

        cut.Find("input").Input("Zephyr");
        cut.Find("input").KeyDown(new KeyboardEventArgs { Key = "Enter" });

        await cut.WaitForAssertionAsync(() =>
        {
            _api.PostRequests.Last().SearchTerm.Should().Be("Zephyr", "Enter must search with the typed text");
            var rows = cut.FindAll(".posts-grid__row");
            rows.Should().ContainSingle("only the matching title is listed")
                .Which.TextContent.Should().Contain("Zephyr Notes");
        });
    }

    [Fact]
    public async Task PostsPage_RowActions_NavigateToTheEditorAnalyticsAndFeedbackTab()
    {
        var post = Summary("a-post", "A Post", PostStatus.Published);
        Setup(post);
        var navigation = Services.GetRequiredService<NavigationManager>();
        var cut = Render<PostsPage>();
        await cut.WaitForAssertionAsync(() => cut.FindAll(".posts-grid__row").Should().ContainSingle());

        ActionFor(cut, "Edit").Click(new MouseEventArgs());
        await cut.WaitForAssertionAsync(() =>
            navigation.Uri.Should().EndWith($"/admin/posts/{post.Id}/edit"));

        ActionFor(cut, "Analytics").Click(new MouseEventArgs());
        await cut.WaitForAssertionAsync(() =>
            navigation.Uri.Should().EndWith($"/admin/posts/{post.Id}/analytics"));

        ActionFor(cut, "Feedback").Click(new MouseEventArgs());
        await cut.WaitForAssertionAsync(() =>
            navigation.Uri.Should().EndWith($"/admin/posts/{post.Id}/edit?tab=feedback",
                "Feedback opens the editor on the Feedback tab"));
    }

    [Fact]
    public async Task PostsPage_Delete_AsksForConfirmation_ThenRemovesTheRow()
    {
        var post = Summary("a-post", "A Post", PostStatus.Published);
        Setup(post);
        var dialogs = Render<MudDialogProvider>();
        var cut = Render<PostsPage>();
        await cut.WaitForAssertionAsync(() => cut.FindAll(".posts-grid__row").Should().ContainSingle());

        ActionFor(cut, "Delete").Click(new MouseEventArgs());

        // The confirm dialog names the post; nothing is deleted until it is confirmed.
        await cut.WaitForAssertionAsync(() => dialogs.Markup.Should().Contain("A Post"));
        _api.DeletedPostIds.Should().BeEmpty("Delete is behind a confirm dialog");
        dialogs.FindAll("button").Single(b => b.TextContent == "Cancel").Click(new MouseEventArgs());
        _api.DeletedPostIds.Should().BeEmpty("cancelling the confirm must not delete");

        ActionFor(cut, "Delete").Click(new MouseEventArgs());
        await cut.WaitForAssertionAsync(() =>
            dialogs.FindAll("button").Should().Contain(b => b.TextContent == "Delete"));
        dialogs.FindAll("button").Single(b => b.TextContent == "Delete").Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() =>
        {
            _api.DeletedPostIds.Should().Equal([post.Id]);
            cut.FindAll(".posts-grid__row").Should().BeEmpty("the deleted post leaves the list");
        });
    }

    [Fact]
    public async Task PostsPage_DeleteFailure_ShowsTheApiError_AndKeepsTheRow()
    {
        var post = Summary("a-post", "A Post", PostStatus.Published);
        Setup(post);
        _api.PostFailure = new HttpRequestException("The API said no.");
        var dialogs = Render<MudDialogProvider>();
        var cut = Render<PostsPage>();
        await cut.WaitForAssertionAsync(() => cut.FindAll(".posts-grid__row").Should().ContainSingle());

        ActionFor(cut, "Delete").Click(new MouseEventArgs());
        await cut.WaitForAssertionAsync(() =>
            dialogs.FindAll("button").Should().Contain(b => b.TextContent == "Delete"));
        dialogs.FindAll("button").Single(b => b.TextContent == "Delete").Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() =>
            cut.Find(".posts__error").TextContent.Should().Be("The API said no."));
        cut.FindAll(".posts-grid__row").Should().ContainSingle("a failed delete keeps the row");
    }

    [Fact]
    public async Task PostsPage_LoadGuard_ASupersededLoadDoesNotClobberTheNewerResult()
    {
        Setup(Summary("the-post", "The Published", PostStatus.Published),
            Summary("the-draft", "The Draft", PostStatus.Draft));
        var release = new TaskCompletionSource();
        var calls = 0;
        _api.PostsLoadDelay = _ => ++calls == 2 ? release.Task : Task.CompletedTask;
        var cut = Render<PostsPage>();
        var grid = cut.FindComponent<PostsGrid>().Instance;
        GridState<PostSummaryDto> state = new() { Page = 0, PageSize = 10 };

        // A stale load in flight while the listing moves on: it completes last and must answer
        // with the newer rows, not the ones it fetched when it started.
        var stale = grid.ServerData!(state, default);
        _api.Posts.Clear();
        _api.Posts.Add(Summary("the-draft", "The Draft", PostStatus.Draft));
        var newer = await grid.ServerData!(state, default);
        newer.Items.Should().ContainSingle().Which.Title.Should().Be("The Draft");

        release.SetResult();
        var staleResult = await stale;

        staleResult.Items.Should().Equal(newer.Items,
            "a superseded load must not clobber the result of the newer one");
    }

    [Fact]
    public async Task PostsPage_LoadFailure_ShowsTheError_TheNextLoad_ClearsIt()
    {
        var post = Summary("the-post", "The Published", PostStatus.Published);
        Setup(post);
        _api.PostsLoadFault = _ => new HttpRequestException("The API said no.");
        var cut = Render<PostsPage>();

        await cut.WaitForAssertionAsync(() =>
            cut.Find(".posts__error").TextContent.Should().Be("The API said no."));

        // The next load succeeds, so the banner from the failed one must go with it.
        _api.PostsLoadFault = null;
        cut.FindAll("button").Single(b => b.TextContent == "All").Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() =>
        {
            cut.FindAll(".posts__error").Should().BeEmpty("the stale error banner must clear on the next load");
            cut.FindAll(".posts-grid__row").Should().ContainSingle();
        });
    }

    [Fact]
    public async Task PostsPage_SupersededFailingLoad_LeavesNoErrorBanner()
    {
        Setup(Summary("the-post", "The Published", PostStatus.Published),
            Summary("the-draft", "The Draft", PostStatus.Draft));
        var release = new TaskCompletionSource();
        var calls = 0;
        var faults = 0;
        // The grid's own initial load is call 1; the held call 2 is the stale one the test drives.
        _api.PostsLoadDelay = _ => ++calls == 2 ? release.Task : Task.CompletedTask;
        _api.PostsLoadFault = _ => ++faults == 2 ? new HttpRequestException("Stale failure.") : null;
        var cut = Render<PostsPage>();
        var grid = cut.FindComponent<PostsGrid>().Instance;
        GridState<PostSummaryDto> state = new() { Page = 0, PageSize = 10 };

        // A slow failing load in flight while a newer one succeeds: when the stale failure finally
        // lands it belongs to a superseded key and must not paint an error banner.
        var stale = grid.ServerData!(state, default);
        _api.Posts.Clear();
        _api.Posts.Add(Summary("the-draft", "The Draft", PostStatus.Draft));
        var newer = await grid.ServerData!(state, default);

        release.SetResult();
        await stale;

        // Re-render to read the page's state (the manual ServerData calls bypass the event
        // pipeline that would normally render it).
        cut.Render();
        newer.Items.Should().ContainSingle("the newer load answered before the stale one");
        cut.FindAll(".posts__error").Should().BeEmpty("a superseded load's failure is not the page's state");
    }

    [Fact]
    public async Task PostsPage_LoadFailure_WithoutAnEmptyStateClaim()
    {
        Setup();
        _api.PostsLoadFault = _ => new HttpRequestException("The API said no.");
        var cut = Render<PostsPage>();

        // A failed load must not claim "No posts found." — the empty state is a claim about
        // successfully loaded data (MediaGrid's guard).
        await cut.WaitForAssertionAsync(() => cut.Find(".posts__error"));
        cut.FindAll(".posts-grid__empty").Should().BeEmpty("a failed load does not know there are no posts");
    }
}
