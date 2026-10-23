using Croft.Shared.Contracts;
using Croft.Web.Components.Layout;
using Bunit;
using Bunit.TestDoubles;
using AwesomeAssertions;

namespace Croft.Web.Tests.Components;

/// <summary>
/// Task R2.2 Step 1: the sidebar's role sections and category list (spec §4.2).
/// </summary>
public class SidebarNavTests : BlogBunitContext
{
    private static IReadOnlyList<CategoryDto> SomeCategories() =>
        [
            new(Guid.NewGuid(), "Programming", "programming", "Code", 3),
            new(Guid.NewGuid(), "Travel", "travel", "Terrain", 1),
        ];

    private static IReadOnlyList<PageNavItemDto> SomePages() =>
        [
            new("About", "about"),
            new("Now", "now"),
        ];

    private IRenderedComponent<SidebarNav> RenderSidebar(
        IReadOnlyList<CategoryDto>? categories = null,
        IReadOnlyList<PageNavItemDto>? pages = null) =>
        Render<SidebarNav>(p => p
            .Add(x => x.Categories, categories ?? SomeCategories())
            .Add(x => x.Pages, pages ?? []));

    private static IReadOnlyList<string?> Hrefs(IRenderedComponent<SidebarNav> cut) =>
        [.. cut.FindAll("a").Select(a => a.GetAttribute("href"))];

    [Fact]
    public void Categories_AreCappedAtTwelve_MostPublishedPostsFirst()
    {
        // 15 in-use categories: the sidebar renders only the top 12 by published-post count so it
        // can never grow into one element per category row.
        var many = Enumerable.Range(1, 15)
            .Select(n => new CategoryDto(Guid.NewGuid(), $"Cat {n:00}", $"cat-{n:00}", null, n))
            .ToList();

        var hrefs = RenderSidebar(many).FindAll("a")
            .Select(a => a.GetAttribute("href"))
            .Where(h => h is { Length: > 0 } && h.StartsWith("/category/"))
            .ToList();

        hrefs.Should().HaveCount(12, "the category list is capped at 12");
        hrefs[0].Should().Be("/category/cat-15", "the most used categories come first");
    }

    [Fact]
    public void MoreCategories_Link_LivesWithTheCategorySection_AndOpensTheDirectory()
    {
        var cut = RenderSidebar();

        var more = cut.Find("a[href='/categories']");
        more.TextContent.Should().Contain("More categories",
            "the rest of the categories live on their paged directory page");
    }

    [Fact]
    public void NoCategoryWithPosts_MeansNoMoreCategoriesLinkEither()
    {
        var cut = RenderSidebar(
        [
            new(Guid.NewGuid(), "Empty", "empty", null, 0),
        ]);

        cut.FindAll("a[href='/categories']").Should().BeEmpty(
            "with no category section there is nothing to top up");
    }

    [Fact]
    public void AdminRole_ShowsAdminSection_AndNotEditorSection()
    {
        Auth.SetAuthorized("admin", AuthorizationState.Authorized);
        Auth.SetRoles("Admin");

        var hrefs = Hrefs(RenderSidebar());

        hrefs.Should().Contain("/admin/posts");
        hrefs.Should().Contain("/admin/posts/new");
        hrefs.Should().Contain("/admin/media");
        hrefs.Should().Contain("/admin/tags");
        hrefs.Should().NotContain("/editor");
    }

    [Fact]
    public void AdminSection_PagesEntry_SitsNextToPosts_AndOnlyForAdmins()
    {
        Auth.SetAuthorized("admin", AuthorizationState.Authorized);
        Auth.SetRoles("Admin");

        var hrefs = Hrefs(RenderSidebar()).ToList();

        hrefs.Should().Contain("/admin/pages", "the admin sidebar lists the pages next to the posts");
        hrefs.IndexOf("/admin/pages").Should().Be(hrefs.IndexOf("/admin/posts") + 1);

        Auth.SetRoles("Editor");
        Hrefs(RenderSidebar()).Should().NotContain("/admin/pages", "the Editor role has no pages list");
    }

    [Fact]
    public void AdminSection_NewPageLink_LivesBelowNewPost()
    {
        Auth.SetAuthorized("admin", AuthorizationState.Authorized);
        Auth.SetRoles("Admin");

        var cut = RenderSidebar();
        var hrefs = Hrefs(cut).ToList();

        hrefs.Should().Contain("/admin/pages/new", "the admin sidebar offers 'New page'");
        hrefs.IndexOf("/admin/pages/new").Should().Be(hrefs.IndexOf("/admin/posts/new") + 1,
            "'New page' sits directly under 'New post'");
        cut.Find("a[href='/admin/pages/new']").TextContent.Should().Contain("New page");
    }

    [Fact]
    public void AdminSection_HiddenRepliesEntry_SitsAfterTags_WithTheVisibilityOffIcon_AndOnlyForAdmins()
    {
        Auth.SetAuthorized("admin", AuthorizationState.Authorized);
        Auth.SetRoles("Admin");

        var cut = RenderSidebar();
        var hrefs = Hrefs(cut).ToList();

        hrefs.Should().Contain("/admin/discussion/blocks");
        hrefs.IndexOf("/admin/discussion/blocks").Should().Be(hrefs.IndexOf("/admin/tags") + 1, "it is the last of the Admin links");
        cut.Find("a[href='/admin/discussion/blocks']").TextContent.Should().Contain("Hidden replies");
        cut.FindComponents<NavItem>().Single(n => n.Instance.Href == "/admin/discussion/blocks").Instance.Icon.Should().Be("VisibilityOff");

        Auth.SetRoles("Editor");
        Hrefs(RenderSidebar()).Should().NotContain("/admin/discussion/blocks", "the Editor role has no Hidden replies page (the Web is Admin only)");
    }

    [Fact]
    public void EditorRole_ShowsEditorSection_AndNotAdminSection()
    {
        Auth.SetAuthorized("editor", AuthorizationState.Authorized);
        Auth.SetRoles("Editor");

        var hrefs = Hrefs(RenderSidebar());

        hrefs.Should().Contain("/editor");
        hrefs.Should().NotContain("/admin/posts");
        hrefs.Should().NotContain("/admin/posts/new");
        hrefs.Should().NotContain("/admin/media");
        hrefs.Should().NotContain("/admin/tags");
    }

    [Fact]
    public void Anonymous_ShowsNeitherAdminNorEditorSection()
    {
        Auth.SetNotAuthorized();

        var hrefs = Hrefs(RenderSidebar());

        hrefs.Should().NotContain("/admin/posts");
        hrefs.Should().NotContain("/editor");
    }

    [Fact]
    public void Categories_WithZeroPosts_AreNotRendered()
    {
        var cut = RenderSidebar(
        [
            new(Guid.NewGuid(), "Empty", "empty", null, 0),
            new(Guid.NewGuid(), "Busy", "busy", null, 2),
        ]);

        var hrefs = Hrefs(cut);

        hrefs.Should().Contain("/category/busy");
        hrefs.Should().NotContain("/category/empty");
    }

    [Fact]
    public void AuthLinks_ForceFullPageLoads()
    {
        // The auth links target the server's /login and /logout endpoints, not Blazor routes.
        // Two opt-outs cover both interception paths: target="_top" makes the interactive Router's
        // link interception skip the anchor (it only claims target-less/_self links), and
        // data-enhance-nav="false" opts out of enhanced navigation before interactivity.
        Auth.SetNotAuthorized();
        var login = RenderSidebar().Find("a[href='/login']");
        login.GetAttribute("target").Should().Be("_top");
        login.GetAttribute("data-enhance-nav").Should().Be("false");

        Auth.SetAuthorized("admin", AuthorizationState.Authorized);
        var logout = RenderSidebar().Find("a[href='/logout']");
        logout.GetAttribute("target").Should().Be("_top");
        logout.GetAttribute("data-enhance-nav").Should().Be("false");
    }

    [Fact]
    public void LongCategoryName_GetsTitleWithTheFullName()
    {
        var name = string.Concat(Enumerable.Repeat("Long category name ", 4)).Substring(0, 60);
        name.Length.Should().Be(60);

        var cut = RenderSidebar(
        [
            new(Guid.NewGuid(), name, "long-name", null, 4),
        ]);

        var anchor = cut.Find("a[href='/category/long-name']");
        anchor.GetAttribute("title").Should().Be(name);
    }

    [Fact]
    public void Pages_RenderInTheHomeSearchSection_AfterSearch_AndAboveCategories()
    {
        // Brief 07g item 3: the marked pages join the Home/Search section, after Search and
        // before the categories — in the order the API gives them (NavigationOrder, then title).
        var cut = RenderSidebar(pages: SomePages());

        var firstSection = cut.FindAll("#sidebar-nav .nav-section")[0];
        var hrefs = firstSection.QuerySelectorAll("a").Select(a => a.GetAttribute("href")).ToList();
        string[] section = ["/", "/search", "/about", "/now"];
        hrefs.Should().Equal(section, "the pages follow Search inside the Home/Search section");

        var all = cut.FindAll("a").Select(a => a.GetAttribute("href")).ToList();
        all.IndexOf("/about").Should().BeLessThan(all.IndexOf("/category/programming"),
            "every page link sits above the category section");
    }

    [Fact]
    public void PageItems_AreNavItems_WithTheDescriptionIconAndATooltipTitle()
    {
        var cut = RenderSidebar(pages: SomePages());

        var about = cut.FindComponents<NavItem>().Single(i => i.Instance.Href == "/about");
        about.Instance.Icon.Should().Be("Description");
        about.Instance.Text.Should().Be("About");
        var anchor = cut.Find("a[href='/about']");
        anchor.GetAttribute("title").Should().Be("About", "the label doubles as the rail tooltip");
    }

    [Fact]
    public void NoMarkedPages_KeepsTheHomeSearchSectionAsBefore()
    {
        var cut = RenderSidebar();

        var firstSection = cut.FindAll("#sidebar-nav .nav-section")[0];
        string[] section = ["/", "/search"];
        firstSection.QuerySelectorAll("a").Select(a => a.GetAttribute("href"))
            .Should().Equal(section);
    }
}
