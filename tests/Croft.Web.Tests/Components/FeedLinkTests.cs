using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Croft.Web.Components.Layout;
using Croft.Web.Components.Pages;
using Croft.Web.Components.Pages.Admin;
using Croft.Web.Components.Pages.Editor;
using Croft.Web.Options;
using Croft.Web.Services;
using Bunit;
using Bunit.Rendering;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Croft.Web.Tests.Components;

/// <summary>
/// Brief 03e item 8: every page's head advertises the Atom feed —
/// <c>&lt;link rel="alternate" type="application/atom+xml" title="{the site's name}" href="{BaseUrl}/feed.xml"&gt;</c>.
/// The framework HeadOutlet renders the content of exactly one HeadContent provider (verified
/// empirically: two providers leave only the last one's content), so the link cannot come from a
/// single App-level HeadContent — it is carried by every head-emitting surface (SeoHead, and each
/// page's own HeadContent beside RobotsNoIndex) and observed here exactly as a browser receives
/// it (a <see cref="HeadOutlet"/> rendered beside the page, like App.razor). One test per routable
/// page component: the SeoHead pages share SeoHead's own emission (covered directly and through
/// Home, the post page incl. its draft/noindex overlap, and the category and tag pages), the
/// no-index pages each carry the link in their own HeadContent. Asserted as exactly one link with
/// the exact attributes — a page missing it, or emitting it twice, fails.
/// </summary>
public class FeedLinkTests : BlogBunitContext
{
    private string BlogName =>
        Services.GetRequiredService<Croft.Web.Branding>().Name;

    private string Origin =>
        Services.GetRequiredService<IOptions<BlogOptions>>().Value.BaseUrl.TrimEnd('/');

    [Fact]
    public async Task SeoHead_AdvertisesTheAtomFeed()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<HeadOutlet>(0);
            builder.CloseComponent();
            builder.OpenComponent<SeoHead>(1);
            builder.AddAttribute(2, nameof(SeoHead.Title), "A page");
            builder.AddAttribute(3, nameof(SeoHead.CanonicalPath), "/a-page");
            builder.AddAttribute(4, nameof(SeoHead.PageType), SeoPageType.Website);
            builder.CloseComponent();
        });

        await ShouldCarryExactlyOneFeedLinkAsync(cut);
    }

    [Fact]
    public async Task Home_AdvertisesTheAtomFeed()
    {
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient());

        var cut = RenderWithHead<Home>();

        await ShouldCarryExactlyOneFeedLinkAsync(cut);
    }

    [Fact]
    public async Task PostPage_Draft_AdvertisesExactlyOneAtomFeed()
    {
        // The draft page's SeoHead wraps RobotsNoIndex in its own HeadContent: the feed link must
        // appear exactly once there too, not once per nested surface.
        var post = Draft();
        var api = new FakeBlogApiClient();
        api.PostsById[post.Id] = post;
        Services.AddSingleton<IBlogApiClient>(api);
        Services.AddSingleton<IMarkdownRenderer, MarkdownRenderer>();

        var cut = Render(builder =>
        {
            builder.OpenComponent<HeadOutlet>(0);
            builder.CloseComponent();
            builder.OpenComponent<Post>(1);
            builder.AddAttribute(2, "Slug", post.Slug);
            builder.CloseComponent();
        });

        await ShouldCarryExactlyOneFeedLinkAsync(cut);
    }

    [Fact]
    public async Task CategoryPage_AdvertisesTheAtomFeed()
    {
        var category = new CategoryDto(Guid.CreateVersion7(), "Field Notes", "field-notes", null, 1);
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient { Categories = [category] });

        var cut = Render(builder =>
        {
            builder.OpenComponent<HeadOutlet>(0);
            builder.CloseComponent();
            builder.OpenComponent<Category>(1);
            builder.AddAttribute(2, "Slug", category.Slug);
            builder.CloseComponent();
        });

        await ShouldCarryExactlyOneFeedLinkAsync(cut);
    }

    [Fact]
    public async Task TagPage_AdvertisesTheAtomFeed()
    {
        var tag = new TagDto(Guid.CreateVersion7(), "Blazor", "blazor", 1, true);
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient { Tags = [tag] });

        var cut = Render(builder =>
        {
            builder.OpenComponent<HeadOutlet>(0);
            builder.CloseComponent();
            builder.OpenComponent<Tag>(1);
            builder.AddAttribute(2, "Slug", tag.Slug);
            builder.CloseComponent();
        });

        await ShouldCarryExactlyOneFeedLinkAsync(cut);
    }

    [Fact]
    public async Task SearchPage_AdvertisesTheAtomFeed()
    {
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient());

        var cut = RenderWithHead<Search>();

        await ShouldCarryExactlyOneFeedLinkAsync(cut);
    }

    [Fact]
    public async Task NotFoundPage_AdvertisesTheAtomFeed()
    {
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient());

        var cut = RenderWithHead<NotFound>();

        await ShouldCarryExactlyOneFeedLinkAsync(cut);
    }

    [Fact]
    public async Task ErrorPage_AdvertisesTheAtomFeed()
    {
        var cut = RenderWithHead<Croft.Web.Components.Pages.Error>();

        await ShouldCarryExactlyOneFeedLinkAsync(cut);
    }

    [Fact]
    public async Task AdminPostsPage_AdvertisesTheAtomFeed()
    {
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient());

        var cut = RenderWithHead<PostsPage>();

        await ShouldCarryExactlyOneFeedLinkAsync(cut);
    }

    [Fact]
    public async Task AdminPostEditPage_AdvertisesTheAtomFeed()
    {
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient());
        Services.AddSingleton<IMarkdownRenderer, MarkdownRenderer>();
        Services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero)));

        var cut = RenderWithHead<PostEditPage>();

        await ShouldCarryExactlyOneFeedLinkAsync(cut);
    }

    [Fact]
    public async Task AdminPostAnalyticsPage_AdvertisesTheAtomFeed()
    {
        var id = Guid.CreateVersion7();
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient
        {
            Analytics = new(id, "A Post", 3, []),
        });
        Services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero)));

        var cut = Render(builder =>
        {
            builder.OpenComponent<HeadOutlet>(0);
            builder.CloseComponent();
            builder.OpenComponent<PostAnalyticsPage>(1);
            builder.AddAttribute(2, nameof(PostAnalyticsPage.Id), id);
            builder.CloseComponent();
        });

        await ShouldCarryExactlyOneFeedLinkAsync(cut);
    }

    [Fact]
    public async Task AdminMediaPage_AdvertisesTheAtomFeed()
    {
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient());

        var cut = RenderWithHead<MediaPage>();

        await ShouldCarryExactlyOneFeedLinkAsync(cut);
    }

    [Fact]
    public async Task AdminTagsPage_AdvertisesTheAtomFeed()
    {
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient());

        var cut = RenderWithHead<TagsPage>();

        await ShouldCarryExactlyOneFeedLinkAsync(cut);
    }

    [Fact]
    public async Task AdminHiddenRepliesPage_AdvertisesTheAtomFeed()
    {
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient());

        var cut = RenderWithHead<HiddenRepliesPage>();

        await ShouldCarryExactlyOneFeedLinkAsync(cut);
    }

    [Fact]
    public async Task EditorDraftsPage_AdvertisesTheAtomFeed()
    {
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient());

        var cut = RenderWithHead<DraftsPage>();

        await ShouldCarryExactlyOneFeedLinkAsync(cut);
    }

    [Fact]
    public async Task EditorDraftReviewPage_AdvertisesTheAtomFeed()
    {
        var post = Draft();
        var api = new FakeBlogApiClient();
        api.PostsById[post.Id] = post;
        Services.AddSingleton<IBlogApiClient>(api);
        Services.AddSingleton<IMarkdownRenderer, MarkdownRenderer>();

        var cut = Render(builder =>
        {
            builder.OpenComponent<HeadOutlet>(0);
            builder.CloseComponent();
            builder.OpenComponent<DraftReviewPage>(1);
            builder.AddAttribute(2, nameof(DraftReviewPage.Slug), post.Slug);
            builder.CloseComponent();
        });

        await ShouldCarryExactlyOneFeedLinkAsync(cut);
    }

    /// <summary>A draft with none of the optional data (same shape as DraftReviewPageTests).</summary>
    private static PostDto Draft() => new(
        Guid.CreateVersion7(), "the-draft", "The Draft", null, "# The hillsideword\n\nBody.", null, null,
        PostStatus.Draft, null,
        new DateTime(2026, 10, 6, 9, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 10, 6, 10, 0, 0, DateTimeKind.Utc),
        "author-id", "Alice Author", null, [], null, 1);

    /// <summary>The single feed discovery link, with the exact attributes the brief mandates.</summary>
    private void AssertOneFeedLink(IRenderedComponent<ContainerFragment> cut)
    {
        var links = cut.FindAll("link[rel='alternate']");
        links.Should().ContainSingle("every page carries exactly one feed discovery link");
        var link = links.Single();
        link.GetAttribute("type").Should().Be("application/atom+xml");
        link.GetAttribute("title").Should().Be(BlogName, "the link's title is the site's name");
        link.GetAttribute("href").Should().Be($"{Origin}/feed.xml", "absolute URLs come from Blog:BaseUrl");
    }

    private async Task ShouldCarryExactlyOneFeedLinkAsync(IRenderedComponent<ContainerFragment> cut) =>
        await cut.WaitForAssertionAsync(() => AssertOneFeedLink(cut));

    /// <summary>Renders a page with a <see cref="HeadOutlet"/> beside it, exactly like App.razor,
    /// so the page's head output is observable in the rendered markup (same shape as
    /// NoIndexTests).</summary>
    private IRenderedComponent<ContainerFragment> RenderWithHead<TPage>() where TPage : IComponent =>
        Render(builder =>
        {
            builder.OpenComponent<HeadOutlet>(0);
            builder.CloseComponent();
            builder.OpenComponent<TPage>(1);
            builder.CloseComponent();
        });
}
