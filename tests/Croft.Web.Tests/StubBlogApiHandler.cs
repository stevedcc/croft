using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Microsoft.AspNetCore.WebUtilities;

namespace Croft.Web.Tests;

/// <summary>
/// The API's public endpoints over a stub handler: paging like the real listing (100 rows per
/// page), but serving whatever rows the test seeded, whatever their status — except the slim
/// sitemap read, which is published-only on the API's side (brief 14b), so the stub applies that rule there.
/// Every list read goes through the paged contract (page/pageSize), except the two bounded sidebar
/// reads: `?top=N` categories and `/api/pages/navigation`; the legacy unpaged arrays are
/// deliberately not served (404), so a whole-table fetch fails the test instead of sneaking back
/// in — these lists grow forever.
/// </summary>
internal sealed class StubBlogApiHandler : HttpMessageHandler
{
    public List<PostSummaryDto> Posts { get; init; } = [];
    public List<CategoryDto> Categories { get; init; } = [];
    public List<TagDto> Tags { get; init; } = [];
    public List<PageSummaryDto> Pages { get; init; } = [];
    public List<PageNavItemDto> PageNav { get; init; } = [];

    public List<PageNavItemDto> FooterNav { get; init; } = [];

    /// <summary>The full-post reads (GET /api/posts/{slug}): the post page's primary fetch.</summary>
    public List<PostDto> PostDetails { get; init; } = [];

    /// <summary>The discussion reads (GET /api/posts/{slug}/discussion, brief 12b) by slug; a slug with none is a 404.</summary>
    public Dictionary<string, Croft.Shared.Contracts.Social.PostDiscussionDto> Discussions { get; } = [];

    /// <summary>Every request the API saw, in order, with its Authorization header.</summary>
    public List<(string PathAndQuery, string? Authorization)> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add((request.RequestUri!.PathAndQuery, request.Headers.Authorization?.ToString()));

        var response = new HttpResponseMessage(HttpStatusCode.OK);
        var path = request.RequestUri.AbsolutePath;
        var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
        if (path is "/api/sitemap")
        {
            // The slim sitemap read, built from the seeded rows by the API's own rules: published posts
            // and pages only (drafts and scheduled posts never), categories and tags that have posts.
            response.Content = JsonContent.Create(new SitemapDto(
                [.. Posts.Where(p => p.Status == PostStatus.Published).Select(p => new SitemapEntryDto(p.Slug, p.UpdatedAt))],
                [.. Pages.Where(p => p.Status == PostStatus.Published).Select(p => new SitemapEntryDto(p.Slug, p.UpdatedAt))],
                [.. Categories.Where(c => c.PostCount > 0).Select(c => c.Slug)],
                [.. Tags.Where(t => t.PostCount > 0).Select(t => t.Slug)]));
            return Task.FromResult(response);
        }

        if (path is "/api/pages/navigation/all")
        {
            // The sidebar's bounded nav read: the published pages marked for the navigation,
            // ordered server-side — the stub serves whatever rows the test seeded.
            response.Content = JsonContent.Create(new PageNavigationDto(PageNav, FooterNav));
            return Task.FromResult(response);
        }

        // The post page's reads: the full post by slug and its related summaries. Served for
        // any slug the test seeded a PostDetail for; unknown slugs stay 404 (the page's 404
        // path). Related mirrors the real endpoint's semantics loosely: other posts, up to
        // count — the pages only render the rows.
        // A page by slug (the static page's fetch): the seeded PostDetails entry of that slug.
        // Unknown slugs stay 404. (Reads only; "/api/pages/navigation/all" is answered above.)
        if (request.Method == HttpMethod.Get && path.StartsWith("/api/pages/", StringComparison.Ordinal))
        {
            var slug = path["/api/pages/".Length..];
            var page = PostDetails.FirstOrDefault(p => p.Slug == slug);
            if (page is null)
            {
                response.StatusCode = HttpStatusCode.NotFound;
                return Task.FromResult(response);
            }

            response.Content = JsonContent.Create(page);
            return Task.FromResult(response);
        }

        // The staff write paths the cache tests drive through the real BlogApiClient: the post by id
        // comes back as the API would answer a write (the seeded detail), a delete answers 204.
        if (request.Method != HttpMethod.Get && path.StartsWith("/api/posts/", StringComparison.Ordinal))
        {
            var id = path["/api/posts/".Length..].Split('/')[0];
            var detail = PostDetails.FirstOrDefault(p => p.Id.ToString() == id);
            if (detail is null)
            {
                response.StatusCode = HttpStatusCode.NotFound;
            }
            else if (request.Method == HttpMethod.Delete)
            {
                response.StatusCode = HttpStatusCode.NoContent;
            }
            else
            {
                response.Content = JsonContent.Create(detail);
            }

            return Task.FromResult(response);
        }

        if (request.Method == HttpMethod.Get && path.StartsWith("/api/posts/", StringComparison.Ordinal) && path.EndsWith("/discussion", StringComparison.Ordinal))
        {
            var slug = path["/api/posts/".Length..^"/discussion".Length];
            if (!Discussions.TryGetValue(slug, out var discussion))
            {
                response.StatusCode = HttpStatusCode.NotFound;
                return Task.FromResult(response);
            }

            response.Content = JsonContent.Create(discussion);
            return Task.FromResult(response);
        }

        if (path.StartsWith("/api/posts/", StringComparison.Ordinal) && path.EndsWith("/related", StringComparison.Ordinal))
        {
            var slug = path["/api/posts/".Length..^"/related".Length];
            var count = query.TryGetValue("count", out var countValue)
                ? int.Parse(countValue.ToString(), CultureInfo.InvariantCulture)
                : 3;
            response.Content = JsonContent.Create(
                Posts.Where(p => p.Slug != slug).Take(count).ToList());
            return Task.FromResult(response);
        }

        if (path.StartsWith("/api/posts/", StringComparison.Ordinal))
        {
            var slug = path["/api/posts/".Length..];
            var detail = PostDetails.FirstOrDefault(p => p.Slug == slug);
            if (detail is null)
            {
                response.StatusCode = HttpStatusCode.NotFound;
                return Task.FromResult(response);
            }

            response.Content = JsonContent.Create(detail);
            return Task.FromResult(response);
        }

        // The category/tag pages' header reads: the by-slug lookups. Unknown slugs stay 404
        // (both pages' real 404 path).
        if (path.StartsWith("/api/categories/", StringComparison.Ordinal))
        {
            var slug = path["/api/categories/".Length..];
            var category = Categories.FirstOrDefault(c => c.Slug == slug);
            if (category is null)
            {
                response.StatusCode = HttpStatusCode.NotFound;
                return Task.FromResult(response);
            }

            response.Content = JsonContent.Create(category);
            return Task.FromResult(response);
        }

        if (path.StartsWith("/api/tags/", StringComparison.Ordinal))
        {
            var slug = path["/api/tags/".Length..];
            var tag = Tags.FirstOrDefault(t => t.Slug == slug);
            if (tag is null)
            {
                response.StatusCode = HttpStatusCode.NotFound;
                return Task.FromResult(response);
            }

            response.Content = JsonContent.Create(tag);
            return Task.FromResult(response);
        }

        if (path is "/api/search")
        {
            // The public search: posts whose title contains the term, paged like the listing.
            var term = query.TryGetValue("q", out var q) ? q.ToString() : string.Empty;
            var hits = Posts.Where(p => p.Title.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
            var searchPage = int.Parse(query["page"].ToString(), CultureInfo.InvariantCulture);
            var searchPageSize = int.Parse(query["pageSize"].ToString(), CultureInfo.InvariantCulture);
            response.Content = PageOf(hits, searchPage, searchPageSize);
            return Task.FromResult(response);
        }

        if (path is "/api/posts" or "/api/categories" or "/api/tags" or "/api/pages")
        {
            if (path is "/api/categories" && query.TryGetValue("top", out var topValue))
            {
                // The sidebar's bounded read (?top=N), not a legacy whole-table fetch.
                response.Content = JsonContent.Create(Categories.Take(
                    int.Parse(topValue.ToString(), CultureInfo.InvariantCulture)));
                return Task.FromResult(response);
            }

            if (!query.TryGetValue("page", out var pageValue) || !query.TryGetValue("pageSize", out var pageSizeValue))
            {
                // The legacy full-array shapes exist only for old external callers; code in this
                // repo reads the paged contract.
                response.StatusCode = HttpStatusCode.NotFound;
                return Task.FromResult(response);
            }

            var page = int.Parse(pageValue.ToString(), CultureInfo.InvariantCulture);
            var pageSize = int.Parse(pageSizeValue.ToString(), CultureInfo.InvariantCulture);
            response.Content = path switch
            {
                "/api/posts" => PageOf(Posts, page, pageSize),
                "/api/categories" => PageOf(Categories, page, pageSize),
                "/api/pages" => PageOf(Pages, page, pageSize),
                _ => PageOf(Tags, page, pageSize),
            };
        }
        else
        {
            response.StatusCode = HttpStatusCode.NotFound;
        }

        return Task.FromResult(response);
    }

    private static HttpContent PageOf<T>(List<T> rows, int page, int pageSize) =>
        JsonContent.Create(new PagedResult<T>([.. rows.Skip((page - 1) * pageSize).Take(pageSize)],
            rows.Count, page, pageSize));
}
