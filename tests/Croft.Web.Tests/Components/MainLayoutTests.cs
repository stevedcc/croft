using Croft.Web.Components.Layout;
using Croft.Web.Services;
using Bunit;
using AwesomeAssertions;
using Croft.Web.Components.Pages;
using Croft.Web.Components.Pages.Admin;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests.Components;

/// <summary>
/// MainLayout is the shell's service component: its sidebar's categories must come from the
/// bounded top read (a cap of 12 by published-post count), never from loading the whole
/// categories table into the browser.
/// </summary>
public class MainLayoutTests : BlogBunitContext
{
    [Fact]
    public async Task SidebarCategories_FetchIsBoundedToTheTop_NeverTheWholeTable()
    {
        var fake = new FakeBlogApiClient
        {
            Categories =
            [
                new(Guid.CreateVersion7(), "Busy", "busy", null, 9),
                new(Guid.CreateVersion7(), "Quiet", "quiet", null, 1),
            ],
        };
        Services.AddSingleton<IBlogApiClient>(fake);
        Services.AddSingleton(new MudBlazor.MudTheme());

        var cut = Render<MainLayout>();

        fake.TopCategoryRequests.Should().ContainSingle().Which.Should().Be(12,
            "the sidebar asks for exactly its cap of top categories");
        fake.CategoryPageRequests.Should().BeEmpty("the shell never pages or lists the whole table");
        cut.FindAll("#sidebar-nav a[href^='/category/']").Select(a => a.GetAttribute("href")).Should().Equal(
            ["/category/busy", "/category/quiet"], "the fetched categories are what the sidebar lists, busiest first");
    }

    [Fact]
    public void SessionEndingWhileTheShellLoads_StillRendersTheLayout_WithoutSidebarData()
    {
        var fake = new FakeBlogApiClient { TopCategoriesFailure = new SessionExpiredException() };
        Services.AddSingleton<IBlogApiClient>(fake);
        Services.AddSingleton(new MudBlazor.MudTheme());

        var cut = Render<MainLayout>();

        cut.Markup.Should().Contain("sidebar-nav", "the shell renders; an exception here would end the circuit");
        cut.FindAll("#sidebar-nav a[href^='/category/']").Should().BeEmpty();
    }

    [Fact]
    public void SidebarAndFooterPages_ComeFromOnePagesNavigationRead_OncePerCircuit()
    {
        var fake = new FakeBlogApiClient
        {
            PageNavItems = [new("About", "about"), new("Now", "now")],
            FooterNavItems = [new("Impressum", "impressum")],
        };
        Services.AddSingleton<IBlogApiClient>(fake);
        Services.AddSingleton(new MudBlazor.MudTheme());

        var cut = Render<MainLayout>();

        fake.PageNavRequests.Should().Be(1,
            "the shell fetches the sidebar's and the footer's pages in one read, beside the categories");
        cut.Find("#sidebar-nav a[href='/about']").TextContent.Should().Contain("About",
            "the fetched pages reach the sidebar");
        cut.Find("footer a[href='/impressum']").TextContent.Should().Contain("Impressum",
            "the fetched footer pages reach the footer");
        cut.FindAll("#sidebar-nav a[href='/impressum']").Should().BeEmpty("a footer page is not in the sidebar");
        cut.FindAll("footer a[href='/about']").Should().BeEmpty("a sidebar page is not in the footer");
    }

    [Fact]
    public void Circuit_RendersTheFourMudBlazorProviders()
    {
        // A circuit has no HttpContext: it only exists for interactive pages.
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient());
        Services.AddSingleton(new MudBlazor.MudTheme());

        AssertAllFourProviders(Render<MainLayout>());
    }

    [Fact]
    public void PrerenderOfAnInteractivePage_RendersTheFourMudBlazorProviders()
    {
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient());
        Services.AddSingleton(new MudBlazor.MudTheme());

        AssertAllFourProviders(Render<MainLayout>(parameters => parameters.AddCascadingValue(HttpContextFor(typeof(PostsPage)))));
    }

    private static void AssertAllFourProviders(IRenderedComponent<MainLayout> cut)
    {
        cut.FindComponents<MudBlazor.MudThemeProvider>().Should().ContainSingle();
        cut.FindComponents<MudBlazor.MudDialogProvider>().Should().ContainSingle();
        cut.FindComponents<MudBlazor.MudSnackbarProvider>().Should().ContainSingle();
        cut.FindComponents<MudBlazor.MudPopoverProvider>().Should().ContainSingle();
    }

    [Fact]
    public void StaticDocument_RendersNoMudBlazorProvider_AndNothingOfTheirMarkup()
    {
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient());
        Services.AddSingleton(new MudBlazor.MudTheme());

        var cut = Render<MainLayout>(parameters => parameters.AddCascadingValue(HttpContextFor(typeof(Home))));

        cut.FindComponents<MudBlazor.MudThemeProvider>().Should().BeEmpty();
        cut.FindComponents<MudBlazor.MudDialogProvider>().Should().BeEmpty();
        cut.FindComponents<MudBlazor.MudSnackbarProvider>().Should().BeEmpty();
        cut.FindComponents<MudBlazor.MudPopoverProvider>().Should().BeEmpty();
        cut.Markup.Should().NotContain("mud-", "a reading page's shell carries no MudBlazor markup or palette variable");
        cut.Find("main").Should().NotBeNull("the shell itself still renders");
    }

    /// <summary>The request for a page: an endpoint whose component is <paramref name="page"/>,
    /// as the Razor components endpoints build them (the render-mode decision reads the page's
    /// ExcludeFromInteractiveRouting attribute through it).</summary>
    private static HttpContext HttpContextFor(Type page)
    {
        var context = new DefaultHttpContext();
        context.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(new ComponentTypeMetadata(page)), "test"));
        return context;
    }
}
