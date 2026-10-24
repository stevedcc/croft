using System.Reflection;
using System.Security.Claims;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Croft.Api.Services;
using Croft.Shared;
using Croft.Shared.Contracts;
using Croft.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace Croft.Api.Tests.Warmup;

/// <summary>
/// Brief 14a item 3, the drift guard: the API warms the query shapes the Web asks for. EF compiles
/// per shape, so a route the Web uses and the warm-up lacks costs the first reader a cold query.
/// Each public read of <see cref="BlogApiClient"/> is called against a capturing handler and its
/// URL, normalised (slug and id to a placeholder, query values to {v}, keys kept), must be in the
/// API warm-up's list normalised the same way. A second test fails when BlogApiClient gains a
/// public method that is neither in the table nor in the explicit "not warmed" list.
/// (The staff typeahead's <c>q=</c> filter is a staff-only shape of GetTagsAsync and
/// GetCategoriesAsync; the table calls the public, unfiltered one.)
/// </summary>
public sealed class WarmupRoutesDriftTests
{
    private static readonly ApiWarmupContent FullContent = new(
        PostSlug: "a-post", CategorySlug: "a-category", TagSlug: "a-tag", PageSlug: "a-page",
        BannerPath: "/api/media/00000000-0000-0000-0000-0000000000e1", SearchWord: "word");

    private static readonly PostListRequest HomeList = new(Page: 1, PageSize: PublicPageSizes.PostList, PublicView: true);

    /// <summary>The Web's public reads, one row per route shape the pages use.</summary>
    public static TheoryData<string, string> PublicShapes => new()
    {
        { nameof(BlogApiClient.GetPostsAsync) + " (home)", nameof(HomeList) },
        { nameof(BlogApiClient.GetPostsAsync) + " (category)", "category" },
        { nameof(BlogApiClient.GetPostsAsync) + " (tag)", "tag" },
        { nameof(BlogApiClient.GetPostAsync), "post" },
        { nameof(BlogApiClient.GetRelatedPostsAsync), "related" },
        { nameof(BlogApiClient.GetDiscussionAsync), "discussion" },
        { nameof(BlogApiClient.GetTagsAsync), "tags" },
        { nameof(BlogApiClient.GetTagBySlugAsync), "tag-by-slug" },
        { nameof(BlogApiClient.GetCategoriesAsync), "categories" },
        { nameof(BlogApiClient.GetTopCategoriesAsync), "top" },
        { nameof(BlogApiClient.GetCategoryBySlugAsync), "category-by-slug" },
        { nameof(BlogApiClient.SearchAsync), "search" },
        { nameof(BlogApiClient.GetPageAsync), "page" },
        { nameof(BlogApiClient.GetPageNavigationsAsync), "navigation" },
    };

    private static Task CallAsync(BlogApiClient client, string which) => which switch
    {
        nameof(HomeList) => client.GetPostsAsync(HomeList),
        "category" => client.GetPostsAsync(new PostListRequest(CategorySlug: "x", Page: 1, PageSize: PublicPageSizes.PostList, PublicView: true)),
        "tag" => client.GetPostsAsync(new PostListRequest(TagSlug: "x", Page: 1, PageSize: PublicPageSizes.PostList, PublicView: true)),
        "post" => client.GetPostAsync("x"),
        "related" => client.GetRelatedPostsAsync("x", PublicPageSizes.Related),
        "discussion" => client.GetDiscussionAsync("x"),
        "tags" => client.GetTagsAsync(1, 100),
        "tag-by-slug" => client.GetTagBySlugAsync("x"),
        "categories" => client.GetCategoriesAsync(1, PublicPageSizes.Categories),
        "top" => client.GetTopCategoriesAsync(PublicPageSizes.SidebarCategories),
        "category-by-slug" => client.GetCategoryBySlugAsync("x"),
        "search" => client.SearchAsync("x", 1, PublicPageSizes.Search),
        "page" => client.GetPageAsync("x"),
        "navigation" => client.GetPageNavigationsAsync(),
        _ => throw new ArgumentOutOfRangeException(nameof(which), which, "not in the table"),
    };

    /// <summary>The BlogApiClient methods a reader's page never calls, each with why the warm-up skips it.</summary>
    private static readonly Dictionary<string, string> NotWarmed = new()
    {
        [nameof(BlogApiClient.CreatePostAsync)] = "write",
        [nameof(BlogApiClient.UpdatePostAsync)] = "write",
        [nameof(BlogApiClient.PublishPostAsync)] = "write",
        [nameof(BlogApiClient.UnpublishPostAsync)] = "write",
        [nameof(BlogApiClient.DeletePostAsync)] = "write",
        [nameof(BlogApiClient.GetPostByIdAsync)] = "staff: the editor's read of a post by id, drafts included; the route is the one GetPostAsync warms",
        [nameof(BlogApiClient.UploadMediaAsync)] = "write",
        [nameof(BlogApiClient.GetTagsByNamesAsync)] = "staff: the editor reads the hashtags of the tags it only knows by name; not a public page read",
        [nameof(BlogApiClient.CreateTagAsync)] = "write",
        [nameof(BlogApiClient.UpdateTagAsync)] = "write",
        [nameof(BlogApiClient.DeleteTagAsync)] = "write",
        [nameof(BlogApiClient.CreateCategoryAsync)] = "write",
        [nameof(BlogApiClient.UpdateCategoryAsync)] = "write",
        [nameof(BlogApiClient.DeleteCategoryAsync)] = "write",
        [nameof(BlogApiClient.GetGlobalMediaAsync)] = "staff: the media library, admin only",
        [nameof(BlogApiClient.GetPostMediaAsync)] = "staff: a post's media list, editors only",
        [nameof(BlogApiClient.UpdateMediaAsync)] = "write",
        [nameof(BlogApiClient.DeleteMediaAsync)] = "write",
        [nameof(BlogApiClient.GetFeedbackAsync)] = "staff: draft feedback",
        [nameof(BlogApiClient.CreateFeedbackAsync)] = "write",
        [nameof(BlogApiClient.ResolveFeedbackAsync)] = "write",
        [nameof(BlogApiClient.GetAnalyticsAsync)] = "staff: a post's analytics",
        [nameof(BlogApiClient.GetDiscussionAdminAsync)] = "staff read",
        [nameof(BlogApiClient.RefreshDiscussionAsync)] = "write",
        [nameof(BlogApiClient.GetAnnouncementsAsync)] = "staff read",
        [nameof(BlogApiClient.AnnounceAsync)] = "write",
        [nameof(BlogApiClient.GetSocialBlocksAsync)] = "staff read",
        [nameof(BlogApiClient.CreateSocialBlockAsync)] = "write",
        [nameof(BlogApiClient.DeleteSocialBlockAsync)] = "write",
    };

    [Theory]
    [MemberData(nameof(PublicShapes))]
    public async Task EveryPublicShapeTheWebAsksFor_IsInTheApiWarmupList(string label, string which)
    {
        var capture = new CapturingHandler();
        var client = new BlogApiClient(new SingleClientFactory(capture), new AnonymousState(), new NoTokens(),
            new TestNavigation(), new SessionExpiryHooks(), new NoOutputCache());

        await CallAsync(client, which);

        var asked = capture.Requests.Should().ContainSingle($"{label} makes one request").Subject;
        var warmed = ApiWarmupRoutes.For(FullContent)
            .Where(route => route.Path is not null)
            .Select(route => Normalise(route.Path!))
            .ToList();
        warmed.Should().Contain(Normalise(asked),
            $"{label} asks the API for {asked}; a shape the warm-up never sends is compiled by the first reader");
    }

    [Fact]
    public void EveryPublicMethodOfTheClient_IsInTheTableOrExplicitlyNotWarmed()
    {
        var inTable = PublicShapes.Select(row => row.Data.Item1.Split(' ')[0]).ToHashSet();
        var methods = typeof(BlogApiClient).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .Where(name => !name.StartsWith("get_", StringComparison.Ordinal))
            .ToHashSet();

        methods.Where(name => !inTable.Contains(name) && !NotWarmed.ContainsKey(name)).Should().BeEmpty(
            "a new BlogApiClient method must go in the drift table or in the not-warmed list with a reason");
        NotWarmed.Keys.Where(name => !methods.Contains(name)).Should().BeEmpty("the not-warmed list names no stale method");
        NotWarmed.Values.Should().OnlyContain(reason => reason.Length > 0);
    }

    [Fact]
    public void TheListAlsoCarriesTheFeedShape_AndSkipsContentThatDoesNotExist()
    {
        var feed = Normalise(PublicReadRoutes.Posts(new PostListRequest(Page: 1, PageSize: PublicPageSizes.FeedEntries)));

        ApiWarmupRoutes.For(FullContent).Select(r => Normalise(r.Path!)).Should().Contain(feed);
        ApiWarmupRoutes.For(new ApiWarmupContent(null, null, null, null, null, "blog"))
            .Where(r => r.Path is null).Select(r => r.Name).Should().NotBeEmpty("a site with no content skips those routes");
    }

    [Fact]
    public async Task EveryRouteTheWebsSitemapAndFeedAskFor_IsInTheApiWarmupList()
    {
        // Brief 14b item 2: the sitemap and the feed build their URLs from PublicReadRoutes too, and the sitemap's
        // slim read is a route of its own. BlogApiClient is not what asks for them, so the table above cannot see them.
        var capture = new CapturingHandler();
        var factory = new SingleClientFactory(capture);
        var options = Microsoft.Extensions.Options.Options.Create(new Croft.Web.Options.BlogOptions { BaseUrl = "https://example.com/" });

        await new SitemapService(factory, options).BuildXmlAsync();
        await new FeedService(factory, options, new Croft.Web.Branding { Name = "Test" }, TimeProvider.System).BuildXmlAsync();

        var warmed = ApiWarmupRoutes.For(FullContent).Where(route => route.Path is not null).Select(route => Normalise(route.Path!)).ToList();
        capture.Requests.Should().NotBeEmpty();
        foreach (var asked in capture.Requests)
        {
            warmed.Should().Contain(Normalise(asked), $"the sitemap or the feed asks the API for {asked}");
        }
    }

    private static string Normalise(string pathAndQuery)
    {
        var split = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        var path = split < 0 ? pathAndQuery : pathAndQuery[..split];
        var segments = path.Split('/');
        for (var i = 1; i < segments.Length; i++)
        {
            if (segments[i - 1] is "posts" or "tags" or "categories" or "pages" && segments[i] != "navigation")
            {
                segments[i] = "{slug}";
            }
            else if (segments[i - 1] == "media")
            {
                segments[i] = "{id}";
            }
        }

        var query = split < 0 ? string.Empty : "?" + Regex.Replace(pathAndQuery[(split + 1)..], "=[^&]*", "={v}");
        return string.Join('/', segments) + query;
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(request.RequestUri.PathAndQuery);
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            if (path.EndsWith("/navigation/all", StringComparison.Ordinal))
            {
                response.Content = JsonContent.Create(new PageNavigationDto([], []));
            }
            else if (path == "/api/sitemap")
            {
                response.Content = JsonContent.Create(new SitemapDto([], [], [], []));
            }
            else if (request.RequestUri.Query.Contains("top=", StringComparison.Ordinal))
            {
                response.Content = JsonContent.Create(Array.Empty<CategoryDto>());
            }
            else if (request.RequestUri.Query.Contains("page=", StringComparison.Ordinal))
            {
                response.Content = JsonContent.Create(new PagedResult<PostSummaryDto>([], 0, 1, 10));
            }
            else
            {
                // A single item or a related list: "not found" is a valid answer the client maps to null or empty.
                response.StatusCode = System.Net.HttpStatusCode.NotFound;
            }

            return Task.FromResult(response);
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler) { BaseAddress = new Uri("http://localhost:5000/") };
    }

    private sealed class AnonymousState : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    private sealed class NoTokens : IUserAccessTokenService
    {
        public Task<string?> GetAccessTokenAsync(ClaimsPrincipal user, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private sealed class NoOutputCache : Microsoft.AspNetCore.OutputCaching.IOutputCacheStore
    {
        public ValueTask EvictByTagAsync(string tag, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<byte[]?> GetAsync(string key, CancellationToken cancellationToken) => ValueTask.FromResult<byte[]?>(null);

        public ValueTask SetAsync(string key, byte[] value, string[]? tags, TimeSpan validFor, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/");

        protected override void NavigateToCore(string uri, bool forceLoad) =>
            throw new InvalidOperationException("an anonymous read never navigates");
    }
}
