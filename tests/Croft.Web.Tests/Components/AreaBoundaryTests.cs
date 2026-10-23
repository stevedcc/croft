using AngleSharp.Dom;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Croft.Web.Components;
using Croft.Web.Components.Admin;
using Croft.Web.Components.Layout;
using Croft.Web.Services;
using Bunit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace Croft.Web.Tests.Components;

/// <summary>
/// The area boundary (spec: the CSP is fixed per document): the staff pages under /admin and
/// /editor allow inline style attributes, the public pages ban them — so every link that crosses
/// between the two areas must load a fresh document (data-enhance-nav="false", which makes
/// Blazor skip the link and lets the browser replace the document and its policy). Links that
/// stay inside their area keep enhanced navigation and are marked with nothing.
/// </summary>
public class AreaBoundaryTests : BlogBunitContext
{
    private static IReadOnlyList<CategoryDto> SomeCategories() =>
        [
            new(Guid.NewGuid(), "Programming", "programming", "Code", 3),
            new(Guid.NewGuid(), "Travel", "travel", "Terrain", 1),
        ];

    private static Func<GridState<PostSummaryDto>, CancellationToken, Task<GridData<PostSummaryDto>>> Data(
        params PostSummaryDto[] posts) =>
        (_, _) => Task.FromResult(new GridData<PostSummaryDto> { Items = posts, TotalItems = posts.Length });

    private static void BeAcrossTheBoundary(IElement link, string because)
    {
        link.GetAttribute("data-enhance-nav").Should().Be("false", because);
        link.GetAttribute("target").Should().Be("_top",
            "and target=_top, so the interactive router also skips the link and the browser loads the document");
    }
    private static void StayEnhanced(IElement link, string because) =>
        link.GetAttribute("data-enhance-nav").Should().BeNull(because);

    private IRenderedComponent<CascadingValue<bool>> RenderInArea<T>(bool inAdminArea, Action<ComponentParameterCollectionBuilder<T>>? configure = null)
        where T : IComponent =>
        Render<CascadingValue<bool>>(parameters => parameters
            .Add(x => x.Name, "InAdminArea")
            .Add(x => x.Value, inAdminArea)
            .AddChildContent<T>(configure ?? (_ => { })));

    [Fact]
    public void SidebarNav_PublicArea_TheStaffLinksLoadFreshDocuments()
    {
        Auth.SetRoles("Admin", "Editor");
        var cut = RenderInArea<SidebarNav>(false, p => p.Add(x => x.Categories, SomeCategories()));

        foreach (var href in new[] { "/admin/posts", "/admin/posts/new", "/admin/media", "/admin/tags", "/admin/discussion/blocks", "/editor" })
        {
            BeAcrossTheBoundary(cut.Find($"a[href='{href}']"),
                $"the staff pages get a new document and their own policy when entered from the public area ({href})");
        }

        StayEnhanced(cut.Find("a[href='/']"), "public links on a public page stay enhanced navigations");
        StayEnhanced(cut.Find("a[href='/search']"), "public links on a public page stay enhanced navigations");
    }

    [Fact]
    public void SidebarNav_StaffArea_ThePublicLinksLoadFreshDocuments()
    {
        Auth.SetRoles("Admin", "Editor");
        var cut = RenderInArea<SidebarNav>(true, p => p.Add(x => x.Categories, SomeCategories()));

        foreach (var href in new[] { "/", "/search", "/categories", "/category/programming", "/category/travel" })
        {
            BeAcrossTheBoundary(cut.Find($"a[href='{href}']"),
                $"the public pages get a new document and their own policy when entered from the staff area ({href})");
        }

        BeAcrossTheBoundary(cut.Find("a.brand"), "the brand link leads to the public home page");
        StayEnhanced(cut.Find("a[href='/admin/posts']"), "staff links on a staff page stay enhanced navigations");
        StayEnhanced(cut.Find("a[href='/editor']"), "staff links on a staff page stay enhanced navigations");
    }

    [Fact]
    public void PostsGrid_StaffArea_ViewToAPublicPost_CrossesTheBoundary()
    {
        var published = new PostSummaryDto(Guid.CreateVersion7(), "hiking-notes", "A Post", null, null,
            "A summary.", 2, PostStatus.Published, new DateTime(2026, 10, 2, 9, 30, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 2, 9, 30, 0, DateTimeKind.Utc), "An Author", null, [], null, 12, 3);
        var draft = published with { Id = Guid.CreateVersion7(), Slug = "unfinished", Status = PostStatus.Draft };
        var cut = RenderInArea<PostsGrid>(true, p => p.Add(x => x.ServerData, Data(published, draft)));

        var view = cut.Find("a.posts-grid__view");
        view.GetAttribute("href").Should().Be("/post/hiking-notes");
        BeAcrossTheBoundary(view, "the View action leaves the staff area for the public post page");

        var draftView = cut.FindAll("a.posts-grid__view")[1];
        draftView.GetAttribute("href").Should().Be("/editor/posts/unfinished");
        StayEnhanced(draftView, "the draft's View action leads to another staff page — the same area");
    }

    [Fact]
    public void PostsGrid_EditorArea_ReviewLinksStayInsideTheArea()
    {
        var draft = new PostSummaryDto(Guid.CreateVersion7(), "unfinished", "The Draft", null, null,
            "A summary.", 2, PostStatus.Draft, null,
            new DateTime(2026, 10, 2, 9, 30, 0, DateTimeKind.Utc), "An Author", null, [], null, null, null);
        var cut = RenderInArea<PostsGrid>(true, p => p
            .Add(x => x.ReadOnly, true)
            .Add(x => x.ServerData, Data(draft)));

        StayEnhanced(cut.Find("a.posts-grid__review"),
            "on the drafts page the Review link stays inside the staff area (both /editor routes)");
    }

    [Fact]
    public void TagPills_AndRelatedPosts_CrossFromTheStaffAreaOnly()
    {
        var tags = new List<TagDto> { new(Guid.NewGuid(), "blazor", "blazor", 0, true) };
        var post = new PostSummaryDto(Guid.CreateVersion7(), "hiking-notes", "A Post", null, null,
            "A summary.", 2, PostStatus.Published, new DateTime(2026, 10, 2, 9, 30, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 2, 9, 30, 0, DateTimeKind.Utc), "An Author", null, [], null, 12, 3);

        var publicTags = Render<TagPills>(p => p.Add(x => x.Tags, tags));
        StayEnhanced(publicTags.Find("a.tag-pill"), "tag links on the public post page stay enhanced navigations");
        var publicRelated = Render<RelatedPosts>(p => p.Add(x => x.Items, [post]));
        StayEnhanced(publicRelated.Find("a.related-posts__link"),
            "related links on the public post page stay enhanced navigations");

        var staffTags = RenderInArea<TagPills>(true, p => p.Add(x => x.Tags, tags));
        BeAcrossTheBoundary(staffTags.Find("a.tag-pill"),
            "tag links leave the draft review page for the public tag page");
        var staffRelated = RenderInArea<RelatedPosts>(true, p => p.Add(x => x.Items, [post]));
        BeAcrossTheBoundary(staffRelated.Find("a.related-posts__link"),
            "related links leave the draft review page for the public post page");
    }

    [Fact]
    public void NotFoundContent_StaffArea_TheLinksLoadFreshDocuments()
    {
        var publicPage = Render<NotFoundContent>();
        StayEnhanced(publicPage.Find("a.mud-button-root"), "the not-found links on a public page stay enhanced");

        var staffPage = RenderInArea<NotFoundContent>(true);
        var links = staffPage.FindAll("a.mud-button-root");
        links.Should().HaveCount(2, "Home and Search (spec §5.5)");
        links.Should().OnlyContain(
            l => l.GetAttribute("data-enhance-nav") == "false" && l.GetAttribute("target") == "_top",
            "the catch-all renders under whatever path missed — from a staff path the links go public");
    }

    [Fact]
    public async Task MainLayout_TheAreaOfTheCurrentPage_DecidesEveryLink()
    {
        Auth.SetRoles("Admin");
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient());
        Services.AddSingleton(new MudTheme());
        var navigation = Services.GetRequiredService<NavigationManager>();

        var cut = Render<MainLayout>();
        await cut.WaitForAssertionAsync(() => cut.Find("a[href='/admin/posts']"));
        StayEnhanced(cut.Find("a[href='/search']"), "on a public page the public links stay enhanced");
        BeAcrossTheBoundary(cut.Find("a[href='/admin/posts']"), "on a public page the staff links cross");

        navigation.NavigateTo("/admin/posts");
        cut.Render();
        await cut.WaitForAssertionAsync(() =>
            cut.Find("a[href='/search']").GetAttribute("data-enhance-nav").Should().Be("false",
                "after the area flips the public links cross (the page re-renders with the new area)"));
        StayEnhanced(cut.Find("a[href='/admin/posts']"), "and the staff links stay inside the staff area");
    }
}
