using Croft.Shared.Contracts;

namespace Croft.Shared;

/// <summary>
/// The URL of every public (anonymous) read of the API, one method per route. The Web's API client,
/// the feed and the sitemap build their requests here, and the API's own warm-up (brief 14a) sends
/// the same ones, so what the warm-up compiles is exactly what readers ask for: EF compiles per
/// query shape, and a <c>?categorySlug=</c> list is a different shape from the plain list. Slugs go
/// in as given, like the call sites before this class.
/// </summary>
public static class PublicReadRoutes
{
    /// <summary>The posts list (Home, Category and Tag pages with <c>PublicView</c>, the feed, the
    /// sitemap, and the staff lists with status and kind): a parameter appears only when set.</summary>
    public static string Posts(PostListRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<string> query = [];
        if (!string.IsNullOrWhiteSpace(request.SearchTerm)) query.Add($"searchTerm={Uri.EscapeDataString(request.SearchTerm)}");
        if (!string.IsNullOrWhiteSpace(request.TagSlug)) query.Add($"tagSlug={Uri.EscapeDataString(request.TagSlug)}");
        if (!string.IsNullOrWhiteSpace(request.CategorySlug)) query.Add($"categorySlug={Uri.EscapeDataString(request.CategorySlug)}");
        if (request.Status.HasValue) query.Add($"status={request.Status.Value}");
        if (request.Kind.HasValue) query.Add($"kind={request.Kind.Value}");
        query.Add($"page={request.Page}");
        query.Add($"pageSize={request.PageSize}");
        if (request.PublicView) query.Add("publicView=true");
        return $"/api/posts?{string.Join("&", query)}";
    }

    public static string Post(string slug) => $"/api/posts/{slug}";

    /// <summary>A post's discussion: the replies from Mastodon and Bluesky, one read (brief 12a).</summary>
    public static string Discussion(string slug) => $"/api/posts/{slug}/discussion";

    public static string Related(string slug, int count) => $"/api/posts/{slug}/related?count={count}";

    public static string Tags(int page, int pageSize, string? q = null) => PagedList("/api/tags", page, pageSize, q);

    public static string Tag(string slug) => $"/api/tags/{slug}";

    public static string Categories(int page, int pageSize, string? q = null) => PagedList("/api/categories", page, pageSize, q);

    public static string TopCategories(int count) => $"/api/categories?top={count}";

    public static string Category(string slug) => $"/api/categories/{slug}";

    public static string Search(string query, int page, int pageSize) =>
        $"/api/search?q={Uri.EscapeDataString(query)}&page={page}&pageSize={pageSize}&publicView=true";

    public static string Page(string slug) => $"/api/pages/{slug}";

    public static string Pages(int page, int pageSize, string? q = null) => PagedList("/api/pages", page, pageSize, q);

    public static string NavigationAll() => "/api/pages/navigation/all";

    /// <summary>The sitemap's one read: published posts and pages with their dates, and the categories and tags that have posts.</summary>
    public static string Sitemap() => "/api/sitemap";

    /// <summary>The list endpoints' shared query shape: page and pageSize always (the paged
    /// contract), q only when there is a filter to send. The staff media lists use it too.</summary>
    public static string PagedList(string path, int page, int pageSize, string? q) =>
        string.IsNullOrWhiteSpace(q)
            ? $"{path}?page={page}&pageSize={pageSize}"
            : $"{path}?page={page}&pageSize={pageSize}&q={Uri.EscapeDataString(q)}";
}
