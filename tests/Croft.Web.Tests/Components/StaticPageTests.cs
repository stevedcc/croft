using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Croft.Web.Components.Pages;
using Croft.Web.Services;
using Bunit;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests.Components;

/// <summary>
/// Brief 07g item 3's public page (Pages/StaticPage, route "/{slug}"): it fetches
/// GET /api/pages/{slug} and composes the shared page surface from it. The 404 outcome (an
/// unknown slug answering HTTP 404 with the not-found copy) is observed where a response status
/// exists — the E2E suite's NotFoundTests — not in bUnit.
/// </summary>
public class StaticPageTests : BlogBunitContext
{
    [Fact]
    public async Task StaticPage_FetchesThePageBySlug_AndRendersTheSurface()
    {
        var page = new PostDto(Guid.CreateVersion7(), "about", "About", "Notes on the owner",
            "## Hello\n\nThe **hillsideword** waits.", null, null, PostStatus.Published,
            new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc),
            "author-id", "Alice Author", null, [], null, 1,
            ContentEditedAt: new DateTime(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc));
        var api = new FakeBlogApiClient();
        api.Pages.Add(page);
        Services.AddSingleton<IBlogApiClient>(api);
        Services.AddSingleton<IMarkdownRenderer>(new MarkdownRenderer());

        var cut = Render<StaticPage>(p => p.Add(x => x.Slug, "about"));

        await cut.WaitForAssertionAsync(() => cut.Find("h1").TextContent.Should().Be("About"));
        string[] fetches = ["about"];
        api.PageSlugRequests.Should().Equal(fetches, "the page is fetched once, by its slug");
        cut.Find(".page-surface__subtitle").TextContent.Should().Be("Notes on the owner");
        cut.Find(".post-meta__when").TextContent.Should().Be("Published on 2 October 2026, last updated on 5 October 2026",
            "the page's date line carries the item's date rule");
        cut.Find(".markdown-body").InnerHtml.Should().Contain("hillsideword",
            "the Markdown body renders in the reading column");
    }
}
