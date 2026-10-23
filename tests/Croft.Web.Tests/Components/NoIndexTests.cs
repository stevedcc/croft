using Croft.Web.Components.Pages;
using Croft.Web.Components.Pages.Admin;
using Croft.Web.Components.Pages.Editor;
using Croft.Web.Services;
using Bunit;
using Bunit.Rendering;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests.Components;

/// <summary>
/// Brief 03e item 5: the pages search engines have no business indexing — the search page, the
/// not-found page, the error page and every admin and editor page — must emit
/// <c>&lt;meta name="robots" content="noindex"&gt;</c>. One of each kind is observed here exactly
/// as a browser receives it (a <see cref="HeadOutlet"/> rendered beside the page, like
/// App.razor). The not-found page's HTTP 404 is asserted together with its meta in the E2E
/// suite's NotFoundTests, which — unlike bUnit — observes the response status. The admin and
/// editor pages render without any authentication here, like PostsPageTests and DraftsPageTests.
/// </summary>
public class NoIndexTests : BlogBunitContext
{
    [Fact]
    public async Task SearchPage_IsMarkedNoindex()
    {
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient());

        var cut = RenderWithHead<Search>();

        await cut.WaitForAssertionAsync(() =>
            cut.Find("meta[name='robots']").GetAttribute("content").Should().Be("noindex"));
    }

    [Fact]
    public async Task ErrorPage_IsMarkedNoindex()
    {
        var cut = RenderWithHead<Croft.Web.Components.Pages.Error>();

        await cut.WaitForAssertionAsync(() =>
            cut.Find("meta[name='robots']").GetAttribute("content").Should().Be("noindex"));
    }

    [Fact]
    public async Task AdminPostsPage_IsMarkedNoindex()
    {
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient());

        var cut = RenderWithHead<PostsPage>();

        await cut.WaitForAssertionAsync(() =>
            cut.Find("meta[name='robots']").GetAttribute("content").Should().Be("noindex"));
    }

    [Fact]
    public async Task AdminHiddenRepliesPage_IsMarkedNoindex()
    {
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient());

        var cut = RenderWithHead<HiddenRepliesPage>();

        await cut.WaitForAssertionAsync(() =>
            cut.Find("meta[name='robots']").GetAttribute("content").Should().Be("noindex"));
    }

    [Fact]
    public async Task EditorDraftsPage_IsMarkedNoindex()
    {
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient());

        var cut = RenderWithHead<DraftsPage>();

        await cut.WaitForAssertionAsync(() =>
            cut.Find("meta[name='robots']").GetAttribute("content").Should().Be("noindex"));
    }

    /// <summary>Renders a page with a <see cref="HeadOutlet"/> beside it, exactly like App.razor,
    /// so the page's head output is observable in the rendered markup (same shape as
    /// SeoHeadTests and HomeSeoHeadTests).</summary>
    private IRenderedComponent<ContainerFragment> RenderWithHead<TPage>() where TPage : IComponent =>
        Render(builder =>
        {
            builder.OpenComponent<HeadOutlet>(0);
            builder.CloseComponent();
            builder.OpenComponent<TPage>(1);
            builder.CloseComponent();
        });
}
