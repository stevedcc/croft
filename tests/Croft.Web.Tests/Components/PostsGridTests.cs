using AngleSharp.Dom;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Croft.Web.Components.Admin;
using Bunit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;

namespace Croft.Web.Tests.Components;

/// <summary>
/// The admin posts grid (spec §6.1/§7.4): the row columns — title linking to the editor, status
/// badge, date ("–" for drafts), category icon, views and open-feedback count — plus the row
/// actions. Edit/Analytics/Feedback/Delete report via the row action callbacks; View links to the
/// public post or the draft review page. ReadOnly (the Editor role) shows Review only.
/// </summary>
public class PostsGridTests : BlogBunitContext
{
    private static PostSummaryDto Post(
        string slug = "a-post",
        string title = "A Post",
        PostStatus status = PostStatus.Published,
        DateTime? publishedAt = null,
        CategoryDto? category = null,
        long? viewCount = 12,
        int? openFeedbackCount = 3,
        PostKind kind = PostKind.Post) =>
        new(Guid.CreateVersion7(), slug, title, null, null, "A summary.", 2, status,
            publishedAt ?? (status == PostStatus.Draft
                ? null
                : new DateTime(2026, 10, 2, 9, 30, 0, DateTimeKind.Utc)),
            new DateTime(2026, 10, 2, 9, 30, 0, DateTimeKind.Utc),
            "An Author", category,
            [], null, viewCount, openFeedbackCount, kind);

    [Fact]
    public void PostsGrid_Row_ShowsTheKind()
    {
        // The admin list shows both kinds; the Kind column is how they told apart.
        var cut = Render<PostsGrid>(p => p.Add(x => x.ServerData, Data(
            Post(title: "The Post"),
            Post(slug: "the-page", title: "The Page", kind: PostKind.Page))));

        RowFor(cut, "The Page").QuerySelector(".posts-grid__kind")!.TextContent.Should().Be("Page");
        RowFor(cut, "The Post").QuerySelector(".posts-grid__kind")!.TextContent.Should().Be("Post");
    }

    private static Func<GridState<PostSummaryDto>, CancellationToken, Task<GridData<PostSummaryDto>>>? Data(
        params PostSummaryDto[] posts) =>
        (_, _) => Task.FromResult(new GridData<PostSummaryDto> { Items = posts, TotalItems = posts.Length });

    private static IElement RowFor(IRenderedComponent<PostsGrid> cut, string title) =>
        cut.FindAll(".posts-grid__row").Single(row => row.TextContent.Contains(title));

    private static IElement ActionFor(IElement row, string name) =>
        row.QuerySelectorAll("a, button").Single(element => element.TextContent == name);

    [Fact]
    public void PostsGrid_PublishedRow_ShowsTheEditorLinkDateCategoryAndCounts()
    {
        var post = Post(category: new CategoryDto(Guid.CreateVersion7(), "Hiking", "hiking", "Terrain", 4));
        var cut = Render<PostsGrid>(p => p.Add(x => x.ServerData, Data(post)));

        var row = RowFor(cut, "A Post");
        var title = row.QuerySelector("a.posts-grid__title");
        title.Should().NotBeNull("the title links to the editor (spec §6.1)");
        title!.TextContent.Should().Be("A Post");
        title.GetAttribute("href").Should().Be($"/admin/posts/{post.Id}/edit");
        row.QuerySelector(".posts-grid__date")!.TextContent.Should().Be("2 October 2026");
        row.QuerySelector(".posts-grid__views")!.TextContent.Should().Be("12");
        row.QuerySelector(".posts-grid__feedback")!.TextContent.Should().Be("3");
        row.QuerySelector(".status-badge")!.TextContent.Should().Be("Published");
        row.QuerySelector(".category-icon").Should().NotBeNull("the category column shows the category icon");
    }

    [Fact]
    public void PostsGrid_DraftRow_ShowsDashes_NoCategoryRendersAnIcon_NothingSaysNull()
    {
        var cut = Render<PostsGrid>(p => p.Add(x => x.ServerData, Data(
            Post(status: PostStatus.Draft, publishedAt: null, category: null, viewCount: null, openFeedbackCount: null))));

        var row = RowFor(cut, "A Post");
        row.QuerySelector(".posts-grid__title").Should().NotBeNull();
        row.QuerySelector(".posts-grid__date")!.TextContent.Should().Be("–", "drafts have no publish date");
        row.QuerySelector(".posts-grid__views")!.TextContent.Should().Be("–");
        row.QuerySelector(".posts-grid__feedback")!.TextContent.Should().Be("–");
        row.QuerySelector(".category-icon").Should().NotBeNull("a post without a category renders the Folder fallback");
        row.TextContent.Should().NotContain("null", "Review Focus 1: missing optional data renders cleanly");
    }

    [Fact]
    public void PostsGrid_ViewAction_LinksToThePublicPostForPublishedPosts()
    {
        var post = Post(slug: "hiking-notes");
        var cut = Render<PostsGrid>(p => p.Add(x => x.ServerData, Data(post)));

        ActionFor(RowFor(cut, "A Post"), "View").GetAttribute("href").Should().Be("/post/hiking-notes");
    }

    [Fact]
    public void PostsGrid_ViewAction_LinksToTheDraftReviewForDraftsAndScheduledPosts()
    {
        // Rule-5-sanctioned test input change: the draft review route moved to /editor/posts/{slug}
        // and this pin's name dropped "DraftReader" with it; the assertion shape is unchanged.
        var draft = Post(title: "The Draft", slug: "unfinished", status: PostStatus.Draft);
        var scheduled = Post(title: "The Scheduled", slug: "queued", status: PostStatus.Scheduled);
        var cut = Render<PostsGrid>(p => p.Add(x => x.ServerData, Data(draft, scheduled)));

        ActionFor(RowFor(cut, "The Draft"), "View").GetAttribute("href").Should().Be("/editor/posts/unfinished");
        ActionFor(RowFor(cut, "The Scheduled"), "View").GetAttribute("href").Should().Be("/editor/posts/queued",
            "each row's View action links to its own draft");
    }

    [Fact]
    public void PostsGrid_ViewAction_LinksToTheRootSlugForPublishedPages()
    {
        // A published page lives at /<slug>, not under /post/ — View must open the page as its
        // readers see it.
        var page = Post(slug: "about", title: "About", kind: PostKind.Page);
        var cut = Render<PostsGrid>(p => p.Add(x => x.ServerData, Data(page)));

        ActionFor(RowFor(cut, "About"), "View").GetAttribute("href").Should().Be("/about");
    }

    [Fact]
    public void PostsGrid_ActionCallbacks_FireWithTheRightRow()
    {
        var first = Post(title: "First", slug: "first");
        var second = Post(title: "Second", slug: "second");
        var edits = new List<PostSummaryDto>();
        var analytics = new List<PostSummaryDto>();
        var feedback = new List<PostSummaryDto>();
        var deletes = new List<PostSummaryDto>();
        var cut = Render<PostsGrid>(p => p
            .Add(x => x.ServerData, Data(first, second))
            .Add(x => x.OnEdit, EventCallback.Factory.Create<PostSummaryDto>(this, edits.Add))
            .Add(x => x.OnAnalytics, EventCallback.Factory.Create<PostSummaryDto>(this, analytics.Add))
            .Add(x => x.OnFeedback, EventCallback.Factory.Create<PostSummaryDto>(this, feedback.Add))
            .Add(x => x.OnDelete, EventCallback.Factory.Create<PostSummaryDto>(this, deletes.Add)));

        string[] actions = ["Edit", "Analytics", "Feedback", "Delete"];
        foreach (var name in actions)
        {
            // Re-fetched per click: each callback re-renders the grid, invalidating old elements.
            ActionFor(RowFor(cut, "Second"), name).Click(new MouseEventArgs());
        }

        edits.Should().Equal([second], "Edit reports the clicked row");
        analytics.Should().Equal([second]);
        feedback.Should().Equal([second]);
        deletes.Should().Equal([second]);
    }

    [Fact]
    public void PostsGrid_ReadOnly_ShowsReviewOnly_NoAdminActionsNoEditorLink()
    {
        var post = Post(slug: "a-draft", status: PostStatus.Draft, publishedAt: null);
        var cut = Render<PostsGrid>(p => p
            .Add(x => x.ServerData, Data(post))
            .Add(x => x.ReadOnly, true));

        var row = RowFor(cut, "A Post");
        row.QuerySelectorAll("button").Should().BeEmpty("the Editor role gets no mutating actions");
        row.QuerySelector("a.posts-grid__title").Should().BeNull("the title must not link to the Admin-only editor");
        row.TextContent.Should().Contain("A Post");
        // Rule-5-sanctioned test input change: the draft review route moved to /editor/posts/{slug}.
        ActionFor(row, "Review").GetAttribute("href").Should().Be("/editor/posts/a-draft");
    }
}
