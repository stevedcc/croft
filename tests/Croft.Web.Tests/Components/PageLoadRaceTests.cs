using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Croft.Web.Components.Pages;
using Croft.Web.Services;
using Bunit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests.Components;

/// <summary>
/// The URL-keyed load guard shared by Home, Category, Tag and Search (spec §5.2/§5.3): Blazor
/// reuses the page instance on query-only navigations, so a slow earlier load can complete after
/// a later one. Only the load whose key still matches may write the page state — a superseded
/// load's data (and its results count) must never clobber the newer one.
/// </summary>
public class PageLoadRaceTests : BlogBunitContext
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    // The category and tag pages list only what carries their slug (the fake filters like the API),
    // so the posts of their tests are seeded with the category or tag the page asks for.
    private static PostSummaryDto Summary(string slug, string title, CategoryDto? category = null, IReadOnlyList<TagDto>? tags = null) =>
        new(Guid.CreateVersion7(), slug, title, null, null, "A summary.", 2, PostStatus.Published,
            Now.UtcDateTime, Now.UtcDateTime, "An Author", category, tags ?? [], null, 4, 1);

    private static readonly TagDto FirstTag = new(Guid.CreateVersion7(), "First Tag", "first", 1, true);
    private static readonly TagDto SecondTag = new(Guid.CreateVersion7(), "Second Tag", "second", 1, true);

    [Fact]
    public async Task Home_ASlowerEarlierLoad_DoesNotClobberTheNewerResult()
    {
        // 11 posts so page 1 (the held load) and page 2 (the newer load) carry different rows.
        var api = new FakeBlogApiClient
        {
            Posts =
            [
                Summary("first", "First Post"),
                .. Enumerable.Range(1, 9).Select(n => Summary($"filler-{n}", $"Filler Post {n}")),
                Summary("second", "Second Post"),
            ],
        };
        var release = new TaskCompletionSource();
        var calls = 0;
        api.PostsLoadDelay = _ => ++calls == 1 ? release.Task : Task.CompletedTask;
        Services.AddSingleton<IBlogApiClient>(api);
        var navigation = Services.GetRequiredService<NavigationManager>();
        var cut = Render<Home>();
        await cut.WaitForAssertionAsync(() => api.PostRequests.Should().HaveCount(1));

        // The user pages on while the first load is still in flight; the newer load answers first.
        navigation.NavigateTo("/?page=2");
        cut.Instance.PageNumber = "2";
        cut.Render();
        await cut.WaitForAssertionAsync(() => api.PostRequests.Should().HaveCount(2));
        release.SetResult();

        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("Second Post"));
        cut.Markup.Should().NotContain("First Post",
            "a superseded load must not clobber the newer result");
    }

    [Fact]
    public async Task Category_ASlowerEarlierLoad_DoesNotClobberTheNewerResult()
    {
        var api = new FakeBlogApiClient
        {
            Categories = [First(), Second()],
            Posts = [Summary("first", "First Post", category: First())],
        };
        var release = new TaskCompletionSource();
        var calls = 0;
        api.PostsLoadDelay = _ => ++calls == 1 ? release.Task : Task.CompletedTask;
        Services.AddSingleton<IBlogApiClient>(api);
        var cut = Render<Croft.Web.Components.Pages.Category>(p => p.Add(x => x.Slug, "first"));
        await cut.WaitForAssertionAsync(() => api.PostRequests.Should().HaveCount(1));

        api.Posts.Clear();
        api.Posts.Add(Summary("second", "Second Post", category: Second()));
        cut.Render(p => p.Add(x => x.Slug, "second"));
        await cut.WaitForAssertionAsync(() => api.PostRequests.Should().HaveCount(2));
        release.SetResult();

        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("Second Post"));
        cut.Markup.Should().NotContain("First Post",
            "a superseded load must not clobber the newer result");
    }

    [Fact]
    public async Task Tag_ASlowerEarlierLoad_DoesNotClobberTheNewerResult()
    {
        var api = new FakeBlogApiClient
        {
            Tags = [FirstTag, SecondTag],
            Posts = [Summary("first", "First Post", tags: [FirstTag])],
        };
        var release = new TaskCompletionSource();
        var calls = 0;
        api.PostsLoadDelay = _ => ++calls == 1 ? release.Task : Task.CompletedTask;
        Services.AddSingleton<IBlogApiClient>(api);
        var cut = Render<Tag>(p => p.Add(x => x.Slug, "first"));
        await cut.WaitForAssertionAsync(() => api.PostRequests.Should().HaveCount(1));

        api.Posts.Clear();
        api.Posts.Add(Summary("second", "Second Post", tags: [SecondTag]));
        cut.Render(p => p.Add(x => x.Slug, "second"));
        await cut.WaitForAssertionAsync(() => api.PostRequests.Should().HaveCount(2));
        release.SetResult();

        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("Second Post"));
        cut.Markup.Should().NotContain("First Post",
            "a superseded load must not clobber the newer result");
    }

    [Fact]
    public async Task Search_ASlowerEarlierLoad_DoesNotClobberTheNewerResultOrItsCount()
    {
        var api = new FakeBlogApiClient
        {
            Posts = [Summary("first", "Alpha First"), Summary("first-two", "Alpha Second")],
        };
        var release = new TaskCompletionSource();
        var calls = 0;
        api.SearchLoadDelay = _ => ++calls == 1 ? release.Task : Task.CompletedTask;
        Services.AddSingleton<IBlogApiClient>(api);
        var navigation = Services.GetRequiredService<NavigationManager>();
        var cut = Render<Search>();
        navigation.NavigateTo("/search?q=Alpha");
        cut.Instance.SearchTerm = "Alpha";
        cut.Render();
        await cut.WaitForAssertionAsync(() => api.SearchRequests.Should().Equal("Alpha"));

        // A second search completes first; the stale first response lands afterwards.
        api.Posts.Clear();
        api.Posts.Add(Summary("second", "Beta Second"));
        navigation.NavigateTo("/search?q=Beta");
        cut.Instance.SearchTerm = "Beta";
        cut.Render();
        await cut.WaitForAssertionAsync(() => api.SearchRequests.Should().Equal("Alpha", "Beta"));
        release.SetResult();

        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("1 result for 'Beta'"));
        cut.Markup.Should().Contain("Beta Second").And.NotContain("Alpha First")
            .And.NotContain("2 results for 'Beta'",
                "the stale response must not clobber the newer result or its results count");
    }

    [Fact]
    public async Task Search_WhileTheFirstFetchIsInFlight_TheHeaderShowsNoStaleOrZeroCount()
    {
        var api = new FakeBlogApiClient { Posts = [Summary("first", "Alpha First")] };
        var release = new TaskCompletionSource();
        api.SearchLoadDelay = _ => release.Task;
        Services.AddSingleton<IBlogApiClient>(api);
        var navigation = Services.GetRequiredService<NavigationManager>();
        var cut = Render<Search>();

        navigation.NavigateTo("/search?q=Alpha");
        cut.Instance.SearchTerm = "Alpha";
        cut.Render();

        // During the fetch there is no loaded result yet: no "0 results for 'Alpha'" and no count
        // under the in-flight query — the header describes the loaded state only.
        cut.Markup.Should().NotContain("0 results for").And.NotContain("results for 'Alpha'")
            .And.NotContain("result for 'Alpha'");
        release.SetResult();

        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("1 result for 'Alpha'"));
    }

    private static CategoryDto First() =>
        new(Guid.CreateVersion7(), "First Category", "first", null, 1);

    private static CategoryDto Second() =>
        new(Guid.CreateVersion7(), "Second Category", "second", null, 1);
}
