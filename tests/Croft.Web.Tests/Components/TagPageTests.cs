using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Croft.Web.Components.Pages;
using Croft.Web.Services;
using Bunit;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests.Components;

/// <summary>
/// The tag page resolves its slug with one lookup: loading the whole tags table into the browser
/// just to find one row does not scale to thousands of tags.
/// </summary>
public class TagPageTests : BlogBunitContext
{
    [Fact]
    public async Task TagPage_ResolvesTheSlugWithOneLookup_NeverTheWholeTable()
    {
        var fake = new FakeBlogApiClient
        {
            Tags = [new(Guid.CreateVersion7(), "k8s", "k8s", 3, true)],
        };
        Services.AddSingleton<IBlogApiClient>(fake);

        var cut = Render<Tag>(p => p.Add(x => x.Slug, "k8s"));

        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("#k8s", "the header shows the resolved tag"));
        string[] oneLookup = ["k8s"];
        fake.TagSlugRequests.Should().Equal(oneLookup, "the page looks the one tag up by slug");
        fake.TagPageRequests.Should().BeEmpty("no tag listing is fetched for a single-tag page");
    }

    [Fact]
    public async Task TagPage_AsksForThePublicView_WhoeverIsSignedIn()
    {
        // Brief 10b item 8: a reading page lists what visitors see, never the admin variant.
        var fake = new FakeBlogApiClient { Tags = [new(Guid.CreateVersion7(), "k8s", "k8s", 3, true)] };
        Services.AddSingleton<IBlogApiClient>(fake);

        var cut = Render<Tag>(p => p.Add(x => x.Slug, "k8s"));

        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("#k8s"));
        fake.PostRequests.Should().NotBeEmpty().And.OnlyContain(r => r.PublicView);
    }

    [Fact]
    public async Task TagPage_ListsOnlyThePostsWithThatTag_AndAsksTheApiForTheTagsPosts()
    {
        // Lose TagSlug from the page's request and every tag page lists every published post on the
        // site: the fake filters by slug like the API, so that shows as the wrong cards here.
        var k8s = new TagDto(Guid.CreateVersion7(), "k8s", "k8s", 1, true);
        var other = new TagDto(Guid.CreateVersion7(), "other", "other", 1, true);
        var fake = new FakeBlogApiClient
        {
            Tags = [k8s, other],
            Posts =
            [
                Summary("tagged-with-k8s", "Tagged With K8s", tags: [k8s]),
                Summary("tagged-with-other", "Tagged With Other", tags: [other]),
                Summary("untagged", "Untagged"),
            ],
        };
        Services.AddSingleton<IBlogApiClient>(fake);

        var cut = Render<Tag>(p => p.Add(x => x.Slug, "k8s"));

        await cut.WaitForAssertionAsync(() => cut.FindAll(".post-card").Should().ContainSingle());
        cut.Find(".post-card__link").GetAttribute("href").Should().Be("/post/tagged-with-k8s");
        cut.Markup.Should().NotContain("Tagged With Other").And.NotContain("Untagged");
        fake.PostRequests.Should().NotBeEmpty().And.OnlyContain(r => r.TagSlug == "k8s",
            "the page asks the API for this tag's posts, not for the whole listing");
    }

    private static PostSummaryDto Summary(string slug, string title, IReadOnlyList<TagDto>? tags = null) =>
        new(Guid.CreateVersion7(), slug, title, null, null, "A summary.", 2, PostStatus.Published,
            new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc),
            "An Author", null, tags ?? [], null, null, null);

    [Theory]
    [InlineData(1, "· 1 post")]
    [InlineData(2, "· 2 posts")]
    public async Task TagPage_HeaderCountsPostsInTheSingular_WhenThereIsOne(int count, string expected)
    {
        var tag = new TagDto(Guid.CreateVersion7(), "k8s", "k8s", count, true);
        var fake = new FakeBlogApiClient
        {
            Tags = [tag],
            Posts = [.. Enumerable.Range(1, count).Select(i => new PostSummaryDto(
                Guid.CreateVersion7(), $"post-{i}", $"Post {i}", null, null, "A summary.", 2, PostStatus.Published,
                new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc),
                "An Author", null, [tag], null, null, null))],
        };
        Services.AddSingleton<IBlogApiClient>(fake);

        var cut = Render<Tag>(p => p.Add(x => x.Slug, "k8s"));

        await cut.WaitForAssertionAsync(() => cut.Find(".page-header__meta").TextContent.Should().Be(expected));
    }
}
