using System.Text.Json;
using System.Text.RegularExpressions;
using Croft.Shared;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Croft.Shared.Warmup;

namespace Croft.Api.Services;

/// <summary>What the API's warm-up found in the content (null: that kind of content does not exist).</summary>
public sealed record ApiWarmupContent(
    string? PostSlug, string? CategorySlug, string? TagSlug, string? PageSlug, string? BannerPath, string SearchWord);

/// <summary>One of the API's warm-up requests: its name and its path (null: skipped).</summary>
public sealed record ApiWarmupRoute(string Name, string? Path);

/// <summary>
/// The API's warm-up requests (brief 14a item 3): the query shapes the Web asks for, built from
/// <see cref="PublicReadRoutes"/> with the page sizes the Web uses (<see cref="PublicPageSizes"/>),
/// so what the warm-up compiles is what the first reader would otherwise pay for. The sitemap is one
/// read of its own since brief 14b (<see cref="PublicReadRoutes.Sitemap"/>), listed last.
/// </summary>
public static class ApiWarmupRoutes
{
    public const string PostsStep = "posts";
    public const string PagesStep = "pages";

    /// <summary>The routes, in the order they are sent; a route whose content is missing has no path.</summary>
    public static IReadOnlyList<ApiWarmupRoute> For(ApiWarmupContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var post = content.PostSlug;
        var postList = (string? slug, string? tag) => PublicReadRoutes.Posts(new PostListRequest(
            TagSlug: tag, CategorySlug: slug, Page: 1, PageSize: PublicPageSizes.PostList, PublicView: true));
        return
        [
            // The Home page's list, and what the pages' shell asks on every render.
            new(PostsStep, postList(null, null)),
            new("sidebar categories", PublicReadRoutes.TopCategories(PublicPageSizes.SidebarCategories)),
            new("navigation", PublicReadRoutes.NavigationAll()),
            // The page list: the first published page comes from it.
            new(PagesStep, PublicReadRoutes.Pages(1, PublicPageSizes.Sitemap)),
            new("post", post is null ? null : PublicReadRoutes.Post(post)),
            new("related", post is null ? null : PublicReadRoutes.Related(post, PublicPageSizes.Related)),
            // The discussion below the post (brief 12a): with social replies off it is an empty answer, but the query shape is compiled all the same.
            new("discussion", post is null ? null : PublicReadRoutes.Discussion(post)),
            new("banner", content.BannerPath),
            new("category posts", content.CategorySlug is { } category ? postList(category, null) : null),
            new("tag posts", content.TagSlug is { } tag ? postList(null, tag) : null),
            new("category", content.CategorySlug is { } c ? PublicReadRoutes.Category(c) : null),
            new("tag", content.TagSlug is { } t ? PublicReadRoutes.Tag(t) : null),
            new("categories", PublicReadRoutes.Categories(1, PublicPageSizes.Categories)),
            // The tags list: the paged shape BlogApiClient.GetTagsAsync asks for (the editor's tag suggestions).
            new("tags", PublicReadRoutes.Tags(1, PublicPageSizes.Sitemap)),
            new("search", PublicReadRoutes.Search(content.SearchWord, 1, PublicPageSizes.Search)),
            new("page", content.PageSlug is { } page ? PublicReadRoutes.Page(page) : null),
            // The feed's list.
            new("feed", PublicReadRoutes.Posts(new PostListRequest(Page: 1, PageSize: PublicPageSizes.FeedEntries))),
            // The sitemap's one slim read, which replaced four paged walks over the lists above.
            new("sitemap", PublicReadRoutes.Sitemap()),
        ];
    }
}

/// <summary>
/// The API's warm-up plan: each step is one of <see cref="ApiWarmupRoutes"/>; the newest published
/// post, its first category and tag, its banner and the first published page are read from the
/// API's own first two answers (the Home list and the page list), so no query exists only for the
/// warm-up.
/// </summary>
public sealed partial class ApiWarmupPlan : IWarmupPlan
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public IReadOnlyList<WarmupStep> Steps { get; } =
    [
        .. ApiWarmupRoutes.For(new ApiWarmupContent(null, null, null, null, null, "blog")).Select(route =>
            new WarmupStep(route.Name,
                results => ApiWarmupRoutes.For(Discover(results)).First(r => r.Name == route.Name).Path,
                KeepBody: route.Name is ApiWarmupRoutes.PostsStep or ApiWarmupRoutes.PagesStep)),
    ];

    private static ApiWarmupContent Discover(WarmupResults results)
    {
        var post = Read<PagedResult<PostSummaryDto>>(results.Body(ApiWarmupRoutes.PostsStep))?.Items is [var newest, ..] ? newest : null;
        var page = Read<PagedResult<PageSummaryDto>>(results.Body(ApiWarmupRoutes.PagesStep))?.Items
            .FirstOrDefault(p => p.Status == PostStatus.Published);
        var banner = post?.Banner?.Url;
        return new ApiWarmupContent(
            post?.Slug,
            post?.Category?.Slug,
            post?.Tags is [var firstTag, ..] ? firstTag.Slug : null,
            page?.Slug,
            banner is not null && banner.StartsWith("/api/media/", StringComparison.Ordinal) ? banner : null,
            post is not null && FirstWord().Match(post.Title) is { Success: true } word ? word.Value : "blog");
    }

    private static T? Read<T>(string? body) where T : class
    {
        if (string.IsNullOrEmpty(body))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(body, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex FirstWord();
}
