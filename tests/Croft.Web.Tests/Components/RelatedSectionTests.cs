using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Croft.Web.Components.Pages;
using Croft.Web.Components.Pages.Editor;
using Croft.Web.Services;
using Bunit;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests.Components;

/// <summary>
/// Brief 07d item 3's "no related posts: no section, no heading at all" rule, at page level: both
/// post surfaces rendered with an empty related list must carry no "Related" heading anywhere.
///
/// Why bUnit and not E2E (controller ruling, disclosed in the item's commit): the API back-fills
/// zero-scoring posts (PostService.RelatedAsync — "zero-scoring posts fill what is left of count"),
/// so an empty related list exists only on a database with no other published post. The E2E suite's
/// instances are shared by fixtures within a run and accumulate posts, Playwright cannot intercept
/// the Web's server-side API call, and deleting other fixtures' posts was rejected on data-hygiene
/// grounds — the empty state cannot be created deterministically there. These tests pin the same
/// rule against the real page components instead (the component-level guard is
/// RelatedPostsTests.RelatedPosts_NoItems_RendersNothing).
/// </summary>
public class RelatedSectionTests : BlogBunitContext
{
    private static readonly DateTime When = new(2026, 10, 8, 9, 0, 0, DateTimeKind.Utc);

    private FakeBlogApiClient _api = null!;

    private FakeBlogApiClient Setup(PostDto post)
    {
        // Related stays empty: the rule under test.
        _api = new FakeBlogApiClient();
        _api.PostsById[post.Id] = post;
        Services.AddSingleton<IBlogApiClient>(_api);
        Services.AddSingleton<IMarkdownRenderer, MarkdownRenderer>();
        return _api;
    }

    private static PostDto Published() => new(
        Guid.CreateVersion7(), "no-related", "No Related", null, "The body text.", null, null,
        PostStatus.Published, When, When, When, "author-id", "Author", null, [], null, 1);

    private static PostDto Draft() => new(
        Guid.CreateVersion7(), "the-draft", "The Draft", null, "The body text.", null, null,
        PostStatus.Draft, null, When, When, "author-id", "Author", null, [], null, 1);

    [Fact]
    public async Task PostPage_NoRelatedPosts_RendersNoRelatedHeadingAnywhere()
    {
        var post = Published();
        Setup(post);
        var cut = Render<Post>(p => p.Add(x => x.Slug, post.Slug));
        await cut.WaitForAssertionAsync(() => cut.Find(".markdown-body"));

        var headings = cut.FindAll("h1, h2, h3, h4, h5, h6");
        headings.Where(h => h.TextContent?.Trim() == "Related").Should().BeEmpty(
            "no related posts means no Related heading anywhere on the page");
        cut.Markup.Should().NotContain("related-posts",
            "no related posts means no related section at all");
    }

    [Fact]
    public async Task DraftReviewPage_NoRelatedPosts_RendersNoRelatedHeadingAnywhere()
    {
        var post = Draft();
        Setup(post);
        var cut = Render<DraftReviewPage>(p => p.Add(x => x.Slug, post.Slug));
        await cut.WaitForAssertionAsync(() => cut.Find(".markdown-body"));

        var headings = cut.FindAll("h1, h2, h3, h4, h5, h6");
        headings.Where(h => h.TextContent?.Trim() == "Related").Should().BeEmpty(
            "no related posts means no Related heading anywhere on the review page");
        cut.Markup.Should().NotContain("related-posts",
            "no related posts means no related section at all");
    }
}
