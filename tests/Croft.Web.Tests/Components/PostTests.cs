using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Croft.Web.Components;
using Croft.Web.Components.Pages;
using Croft.Web.Services;
using Bunit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests.Components;

/// <summary>
/// The /post/{slug} route is posts-only. A published page does load there through
/// GET /api/posts/{slug} — the API deliberately serves pages to the editor (see the API suite's
/// Pages/Posts controller tests) — but the route must answer 404 for it instead of rendering it
/// as a post, for anonymous readers and signed-in editors alike. Without the gate the same page
/// is indexable twice with conflicting signals: /post/{slug} (canonical /post/{slug},
/// og:type article, BlogPosting JSON-LD, post chrome) and /{slug} (canonical /{slug},
/// og:type website, WebPage JSON-LD) — "Pages never appear among posts" includes the post route.
/// The post page's rendering is covered by SeoHeadTests, PageTitleTests and RelatedSectionTests;
/// this file pins the route's kind gate. The 404's HTTP status and not-found copy are asserted
/// end to end in Croft.Web.E2E.StaticPageTests — unlike bUnit, the E2E suite observes the
/// response status (same split as the missing-post 404 in Croft.Web.E2E.NotFoundTests).
/// </summary>
public class PostTests : BlogBunitContext
{
    private static readonly DateTime When = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PageKindPost_TriggersNotFound_AndRendersNoPostChrome(bool signedInEditor)
    {
        var page = Page();
        var api = new FakeBlogApiClient();
        api.PostsById[page.Id] = page;
        Services.AddSingleton<IBlogApiClient>(api);
        Services.AddSingleton<IMarkdownRenderer, MarkdownRenderer>();
        if (signedInEditor)
        {
            Auth.SetRoles("Admin");
        }

        var notFound = false;
        Services.GetRequiredService<NavigationManager>().OnNotFound += (_, _) => notFound = true;

        var cut = Render<Post>(p => p.Add(x => x.Slug, page.Slug));

        await cut.WaitForAssertionAsync(() => notFound.Should().BeTrue(
            "the /post/ route is posts-only: a page must 404 there even for a signed-in editor"));
        cut.HasComponent<PostHero>().Should().BeFalse("a page must not render post chrome");
        cut.Markup.Should().NotContain("post-layout",
            "no post body layout, no rail and no related posts on the post route");
        cut.Markup.Should().NotContain("markdown-body", "the page's body must not render as a post body");
    }

    [Fact]
    public async Task Post_WithNoSuchSlug_TriggersNotFound()
    {
        var cut = RenderForNotFound<Post>(p => p.Add(x => x.Slug, "no-such-post"), out var notFound);

        await cut.WaitForAssertionAsync(() => notFound().Should().BeTrue("a missing post answers the not-found page"));
    }

    [Fact]
    public async Task Category_WithNoSuchSlug_TriggersNotFound()
    {
        var cut = RenderForNotFound<Category>(p => p.Add(x => x.Slug, "no-such-category"), out var notFound);

        await cut.WaitForAssertionAsync(() => notFound().Should().BeTrue("an unknown category slug answers the not-found page"));
    }

    [Fact]
    public async Task Tag_WithNoSuchSlug_TriggersNotFound()
    {
        var cut = RenderForNotFound<Tag>(p => p.Add(x => x.Slug, "no-such-tag"), out var notFound);

        await cut.WaitForAssertionAsync(() => notFound().Should().BeTrue("an unknown tag slug answers the not-found page"));
    }

    [Fact]
    public async Task StaticPage_WithNoSuchSlug_TriggersNotFound()
    {
        var cut = RenderForNotFound<StaticPage>(p => p.Add(x => x.Slug, "no-such-page"), out var notFound);

        await cut.WaitForAssertionAsync(() => notFound().Should().BeTrue("an unknown page slug answers the not-found page"));
    }

    [Fact]
    public void TheNotFoundPage_ReachedByItsCatchAllRoute_RaisesARealNotFound()
    {
        // The catch-all binds {**path}: Path is set exactly when it matched a URL no page route owns,
        // and only then does the page turn the request into a real 404. Reached as the Router's NotFoundPage
        // (or /not-found) Path is null and the page just renders (answering 200).
        var notFound = false;
        Services.GetRequiredService<NavigationManager>().OnNotFound += (_, _) => notFound = true;

        Render<NotFound>(p => p.Add(x => x.Path, "no/such/url"));
        notFound.Should().BeTrue("a URL no @page route matches must answer 404, not render the not-found copy with 200");

        notFound = false;
        Render<NotFound>();
        notFound.Should().BeFalse("/not-found itself (Path null) answers 200 so a redirect to it is distinguishable from a real 404");
    }

    /// <summary>Renders a page whose data the API does not have and reports whether it asked for a 404.</summary>
    private IRenderedComponent<T> RenderForNotFound<T>(Action<ComponentParameterCollectionBuilder<T>> parameters, out Func<bool> notFound)
        where T : IComponent
    {
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient());
        var asked = false;
        Services.GetRequiredService<NavigationManager>().OnNotFound += (_, _) => asked = true;
        notFound = () => asked;
        return Render(parameters);
    }

    /// <summary>A published page, as the API returns it from GET /api/posts/{slug}.</summary>
    private static PostDto Page() => new(
        Guid.CreateVersion7(), "about", "About", null, "## The page\n\nBody paragraph.", "The description.",
        null, PostStatus.Published, When, When, When, "author-id", "Author", null, [], null, 1,
        PostKind.Page, NavigationPlacement.None, 0, When);
}
