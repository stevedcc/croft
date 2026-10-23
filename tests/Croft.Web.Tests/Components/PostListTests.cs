using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Croft.Web.Components;
using Bunit;
using AwesomeAssertions;

namespace Croft.Web.Tests.Components;

/// <summary>
/// Task R3.2: PostList (spec §7.3) — one Featured card first when ShowFeatured on page 1, the rest
/// as List cards, the pager below, and an EmptyText message when there is nothing to show.
/// </summary>
public class PostListTests : BlogBunitContext
{
    private static PostSummaryDto Post(string title, string slug) =>
        new(Guid.NewGuid(), slug, title, null, null, "Summary text.", 5,
            PostStatus.Published, new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc),
            "An Author", null, [], null, null, null);

    private static IReadOnlyList<PostSummaryDto> ThreePosts() =>
        [
            Post("First", "first"),
            Post("Second", "second"),
            Post("Third", "third"),
        ];

    [Fact]
    public void PostList_ShowFeatured_FirstItemFeaturedRestList()
    {
        var cut = Render<PostList>(p => p
            .Add(x => x.Items, ThreePosts())
            .Add(x => x.ShowFeatured, true)
            .Add(x => x.Page, 1)
            .Add(x => x.TotalPages, 1));

        var cards = cut.FindAll(".post-card");
        cards.Should().HaveCount(3);
        cards[0].ClassList.Should().Contain("post-card--featured");
        cards[0].QuerySelector(".post-card__overlay").Should().NotBeNull("the featured title sits over the image");
        cards[1].ClassList.Should().Contain("post-card--list");
        cards[2].ClassList.Should().Contain("post-card--list");
    }

    [Fact]
    public void PostList_ThreadsQueryStringToPager()
    {
        // Task R3.3, ruling R3-3-1: PostList carries the caller's query down to PagePager so the
        // search results keep ?q= across paging.
        var cut = Render<PostList>(p => p
            .Add(x => x.Items, ThreePosts())
            .Add(x => x.Page, 2)
            .Add(x => x.TotalPages, 3)
            .Add(x => x.QueryString, "q=hello"));

        var links = cut.FindAll(".page-pager a").Select(l => l.GetAttribute("href"));
        links.Should().Equal("?q=hello&page=1", "?q=hello&page=2", "?q=hello&page=3");
    }

    [Fact]
    public void PostList_ThreadsPathAndQueryStringToPager()
    {
        // Task R3.3, ruling R3-3-2: PostList threads both optional link parameters to PagePager.
        var cut = Render<PostList>(p => p
            .Add(x => x.Items, ThreePosts())
            .Add(x => x.Page, 2)
            .Add(x => x.TotalPages, 3)
            .Add(x => x.Path, "/search")
            .Add(x => x.QueryString, "q=hello"));

        var links = cut.FindAll(".page-pager a").Select(l => l.GetAttribute("href"));
        links.Should().Equal("/search?q=hello&page=1", "/search?q=hello&page=2", "/search?q=hello&page=3");
    }

    [Fact]
    public void PostList_NoFeatured_AllList()
    {
        var cut = Render<PostList>(p => p
            .Add(x => x.Items, ThreePosts())
            .Add(x => x.ShowFeatured, false)
            .Add(x => x.Page, 1)
            .Add(x => x.TotalPages, 1));

        var cards = cut.FindAll(".post-card");
        cards.Should().HaveCount(3);
        cards.Should().OnlyContain(c => c.ClassList.Contains("post-card--list"));
        cut.FindAll(".post-card__overlay").Should().BeEmpty();
    }

    [Theory]
    [InlineData("Nothing published yet.")]
    [InlineData("No posts yet")]
    public void PostList_Empty_ShowsEmptyMessage(string emptyText)
    {
        var cut = Render<PostList>(p => p
            .Add(x => x.Items, [])
            .Add(x => x.EmptyText, emptyText));

        cut.Find(".post-list__empty").TextContent.Should().Be(emptyText);
        cut.FindAll(".post-card").Should().BeEmpty();
    }

    [Fact]
    public void PostList_MultiplePages_ShowsPager()
    {
        var cut = Render<PostList>(p => p
            .Add(x => x.Items, ThreePosts())
            .Add(x => x.Page, 2)
            .Add(x => x.TotalPages, 3));

        cut.FindAll(".page-pager a").Should().HaveCount(3);
    }

    [Fact]
    public void PostList_EmptyPageBeyondRange_ShowsEmptyMessageAndPagerBackToPage1()
    {
        // Task R3.6, ruling R3-3-3: a stale/out-of-range ?page=99 shows the empty state with no way
        // back — the pager must render whenever TotalPages > 1, even with zero items, so the reader
        // can page back. Empty-state copy stays as R3.3 delivered it.
        var cut = Render<PostList>(p => p
            .Add(x => x.Items, [])
            .Add(x => x.Page, 99)
            .Add(x => x.TotalPages, 3));

        cut.Find(".post-list__empty").TextContent.Should().Be("Nothing published yet.");
        var links = cut.FindAll(".page-pager a").Select(l => l.GetAttribute("href"));
        links.Should().Equal("?page=1", "?page=2", "?page=3");
    }

    [Fact]
    public void PostList_EmptySinglePage_ShowsEmptyMessageWithoutPager()
    {
        // The other half of ruling R3-3-3: "whenever TotalPages > 1" — a one-page list still shows
        // no pager (PagePager's own contract), empty or not.
        var cut = Render<PostList>(p => p
            .Add(x => x.Items, [])
            .Add(x => x.TotalPages, 1));

        cut.Find(".post-list__empty").TextContent.Should().Be("Nothing published yet.");
        cut.FindAll(".page-pager").Should().BeEmpty();
    }
}
