using Croft.Shared.Contracts;
using Croft.Web.Components.Layout;
using Croft.Web.Services;
using Bunit;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests.Components;

/// <summary>
/// The prerender-to-interactive handoff of the shell's sidebar data (W-023): <c>MainLayout</c> persists
/// the categories and page navigation it fetched, and the interactive render restores them instead of
/// fetching again. Without these tests the handoff ran in no test: delete the restore branch and every
/// admin page load fetches twice; restore the state but forget to assign it and the sidebar is empty
/// (review 2026-10-19, I-13).
/// </summary>
public class MainLayoutPersistentStateTests : BlogBunitContext
{
    /// <summary>
    /// The key the framework derives for <c>MainLayout.State</c> (from the component type, the
    /// property name and the parent component). The persist test below reads the state back under
    /// this key, so a renamed property or component, or a changed derivation, fails that test by
    /// naming this constant.
    /// </summary>
    private const string StateKey = "N7Svlgyb531Dr3OUtI2XRoK4Hh29YRvUbHD7Tb3TY6w=";

    private static readonly CategoryDto Busy = new(Guid.CreateVersion7(), "Busy", "busy", null, 9);

    [Fact]
    public void ARenderThatFetchedTheSidebarData_PersistsExactlyWhatItFetched()
    {
        var fake = new FakeBlogApiClient
        {
            Categories = [Busy],
            PageNavItems = [new("About", "about")],
            FooterNavItems = [new("Impressum", "impressum")],
        };
        Services.AddSingleton<IBlogApiClient>(fake);
        Services.AddSingleton(new MudBlazor.MudTheme());

        Render<MainLayout>();
        PersistentState.TriggerOnPersisting();

        PersistentState.TryTake<MainLayout.MainLayoutState>(StateKey, out var state).Should().BeTrue(
            "the layout persists its sidebar data for the interactive render; if this fails the framework's key for "
            + "MainLayout.State changed (update StateKey) or nothing was persisted");
        state!.Categories.Should().Equal(Busy);
        state.Pages.Should().ContainSingle().Which.Slug.Should().Be("about");
        state.FooterPages.Should().ContainSingle().Which.Slug.Should().Be("impressum");
    }

    [Fact]
    public void ARenderWithRestoredState_FetchesNothing_AndShowsTheRestoredSidebar()
    {
        var fake = new FakeBlogApiClient();
        Services.AddSingleton<IBlogApiClient>(fake);
        Services.AddSingleton(new MudBlazor.MudTheme());
        PersistentState.Persist(StateKey, new MainLayout.MainLayoutState(
            [Busy], [new PageNavItemDto("About", "about")], [new PageNavItemDto("Impressum", "impressum")]));

        var cut = Render<MainLayout>();

        fake.TopCategoryRequests.Should().BeEmpty("the prerender already fetched the categories: the circuit must not fetch them again");
        fake.PageNavRequests.Should().Be(0, "nor the page navigation");
        cut.Find("#sidebar-nav a[href='/category/busy']").TextContent.Should().Contain("Busy",
            "the restored categories reach the sidebar");
        cut.Find("#sidebar-nav a[href='/about']").TextContent.Should().Contain("About",
            "the restored pages reach the sidebar");
    }

    [Fact]
    public void WithoutRestoredState_TheLayoutFetchesOnceAndShowsTheFetchedSidebar()
    {
        var fake = new FakeBlogApiClient { Categories = [Busy], PageNavItems = [new("About", "about")] };
        Services.AddSingleton<IBlogApiClient>(fake);
        Services.AddSingleton(new MudBlazor.MudTheme());

        var cut = Render<MainLayout>();

        fake.TopCategoryRequests.Should().ContainSingle();
        fake.PageNavRequests.Should().Be(1);
        cut.Find("#sidebar-nav a[href='/category/busy']").TextContent.Should().Contain("Busy");
    }
}
