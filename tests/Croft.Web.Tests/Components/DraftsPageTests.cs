using AngleSharp.Dom;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Croft.Web.Components.Admin;
using Croft.Web.Components.Pages.Editor;
using Croft.Web.Services;
using Bunit;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace Croft.Web.Tests.Components;

/// <summary>
/// The Editor role's list (spec §6.6): drafts and scheduled posts merged into one read-only grid
/// — drafts first, each in the API's order — paged as one list, with published posts excluded and
/// the grid's empty state when the editor has nothing to review.
/// </summary>
public class DraftsPageTests : BlogBunitContext
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

    private FakeBlogApiClient _api = null!;

    private FakeBlogApiClient Setup(params PostSummaryDto[] posts)
    {
        _api = new FakeBlogApiClient { Posts = [.. posts] };
        Services.AddSingleton<IBlogApiClient>(_api);
        return _api;
    }

    /// <summary>In seeded (API) order; drafts carry no publish date.</summary>
    private static PostSummaryDto Summary(string slug, string title, PostStatus status, int minute = 0) =>
        new(Guid.CreateVersion7(), slug, title, null, null, "A summary.", 2, status,
            status == PostStatus.Draft ? null : Now.UtcDateTime.AddMinutes(minute), Now.UtcDateTime, "An Author", null, [], null, null, 0);

    private static string[] Titles(IEnumerable<IElement> rows) =>
        [.. rows.Select(r => r.QuerySelector(".posts-grid__title")!.TextContent)];

    [Fact]
    public async Task DraftsPage_MergesDraftsFirstThenScheduled_ExcludingPublished()
    {
        Setup(Summary("first", "First Draft", PostStatus.Draft),
            Summary("second", "Second Draft", PostStatus.Draft),
            Summary("queued", "The Scheduled", PostStatus.Scheduled),
            Summary("live", "The Published", PostStatus.Published));

        var cut = Render<DraftsPage>();
        await cut.WaitForAssertionAsync(() => cut.FindAll(".posts-grid__row").Should().HaveCount(3));

        Titles(cut.FindAll(".posts-grid__row")).Should().Equal(
            ["First Draft", "Second Draft", "The Scheduled"],
            because: "the page composes the two status listings into one drafts-then-scheduled list");
        cut.Markup.Should().NotContain("The Published", "published posts are not the editor's to review");
    }

    [Fact]
    public async Task DraftsPage_MergedRows_PageAsOneList()
    {
        var drafts = Enumerable.Range(1, 11)
            .Select(i => Summary($"draft-{i:D2}", $"Draft {i:D2}", PostStatus.Draft));
        var scheduled = new[]
        {
            Summary("scheduled-a", "Scheduled A", PostStatus.Scheduled, minute: 2),
            Summary("scheduled-b", "Scheduled B", PostStatus.Scheduled, minute: 1),
        };
        Setup([.. drafts, .. scheduled]);

        var cut = Render<DraftsPage>();
        await cut.WaitForAssertionAsync(() => cut.FindAll(".posts-grid__row").Should().HaveCount(10));

        // The grid's server callback pages the merged list: page 2 continues where page 1 stopped.
        var load = cut.FindComponent<PostsGrid>().Instance.ServerData!;
        var page1 = await load(new GridState<PostSummaryDto> { Page = 0, PageSize = 10 }, CancellationToken.None);
        var page2 = await load(new GridState<PostSummaryDto> { Page = 1, PageSize = 10 }, CancellationToken.None);

        page1.Items.Select(p => p.Title).Should().Equal(
            "Draft 01", "Draft 02", "Draft 03", "Draft 04", "Draft 05",
            "Draft 06", "Draft 07", "Draft 08", "Draft 09", "Draft 10");
        page2.Items.Select(p => p.Title).Should().Equal(
            ["Draft 11", "Scheduled A", "Scheduled B"],
            because: "the merged rows page as one list, not one page per status");
        page1.TotalItems.Should().Be(13, "the merged total spans both statuses");
        page2.TotalItems.Should().Be(13);
    }

    [Fact]
    public async Task DraftsPage_NoDrafts_ShowsTheGridEmptyState()
    {
        Setup();

        var cut = Render<DraftsPage>();
        await cut.WaitForAssertionAsync(() => cut.Find(".posts-grid__empty"));

        cut.Find(".posts-grid__empty").TextContent.Should().Be("No posts found.",
            "Review Focus 5: /editor with no drafts shows a sane empty state");
        cut.FindAll(".posts-grid__row").Should().BeEmpty();
    }

    [Fact]
    public async Task DraftsPage_LoadFailure_ShowsTheError_TheNextLoad_ClearsIt()
    {
        var post = Summary("the-draft", "The Draft", PostStatus.Draft);
        Setup(post);
        _api.PostsLoadFault = _ => new HttpRequestException("The API said no.");
        var cut = Render<DraftsPage>();

        await cut.WaitForAssertionAsync(() =>
            cut.Find(".drafts__error").TextContent.Should().Be("The API said no."));

        // The next load succeeds, so the banner from the failed one must go with it.
        _api.PostsLoadFault = null;
        await cut.InvokeAsync(() => cut.FindComponent<PostsGrid>().Instance.ReloadAsync());

        await cut.WaitForAssertionAsync(() =>
        {
            cut.FindAll(".drafts__error").Should().BeEmpty("the stale error banner must clear on the next load");
            cut.FindAll(".posts-grid__row").Should().ContainSingle();
        });
    }

    [Fact]
    public async Task DraftsPage_SupersededFailingLoad_LeavesNoErrorBanner()
    {
        Setup(Summary("first", "First Draft", PostStatus.Draft),
            Summary("second", "Second Draft", PostStatus.Draft));
        var release = new TaskCompletionSource();
        var calls = 0;
        var faults = 0;
        // Each load fetches both statuses (two API calls): the grid's own initial load is calls
        // 1-2; the held call 3 is the stale manual load's first fetch, which then fails late.
        _api.PostsLoadDelay = _ => ++calls == 3 ? release.Task : Task.CompletedTask;
        _api.PostsLoadFault = _ => ++faults == 3 ? new HttpRequestException("Stale failure.") : null;
        var cut = Render<DraftsPage>();
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
        cut.FindAll(".drafts__error").Should().BeEmpty("a superseded load's failure is not the page's state");
    }

    [Fact]
    public async Task DraftsPage_LoadFailure_WithoutAnEmptyStateClaim()
    {
        Setup();
        _api.PostsLoadFault = _ => new HttpRequestException("The API said no.");
        var cut = Render<DraftsPage>();

        // A failed load must not claim "No posts found." — the empty state is a claim about
        // successfully loaded data (MediaGrid's guard).
        await cut.WaitForAssertionAsync(() => cut.Find(".drafts__error"));
        cut.FindAll(".posts-grid__empty").Should().BeEmpty("a failed load does not know there are no drafts");
    }
}
