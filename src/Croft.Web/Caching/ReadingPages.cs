using Croft.Web.Components.Pages;

namespace Croft.Web.Caching;

/// <summary>One reading page the output cache keeps: its route, its component, and the query
/// parameters it reads (the only part of the query that is part of the cache key).</summary>
public sealed record ReadingPageRoute(string Pattern, Type Page, IReadOnlyList<string> QueryKeys);

/// <summary>
/// The reading pages whose anonymous renders are cached (brief 14b). Search, the not-found and
/// error pages, the admin pages and everything else are not here. A page that starts reading
/// another query parameter must list it, or two different pages would share one entry
/// (<c>ReadingPageCacheRegistryTests</c> reads the page components and fails until it does).
/// </summary>
public static class ReadingPages
{
    /// <summary>The query parameter every list page reads: the page number.</summary>
    public const string PageKey = "page";

    public static IReadOnlyList<ReadingPageRoute> Routes { get; } =
    [
        new("/", typeof(Home), [PageKey]),
        new("/post/{slug}", typeof(Post), []),
        new("/{slug}", typeof(StaticPage), []),
        new("/category/{slug}", typeof(Category), [PageKey]),
        new("/tag/{slug}", typeof(Tag), [PageKey]),
        new("/categories", typeof(Categories), [PageKey]),
    ];

    /// <summary>The reading page the request was routed to, or null.</summary>
    public static ReadingPageRoute? Match(HttpContext context)
    {
        if (context.GetEndpoint() is not RouteEndpoint { RoutePattern.RawText: { } raw })
        {
            return null;
        }

        var pattern = raw.StartsWith('/') ? raw : "/" + raw;
        foreach (var route in Routes)
        {
            if (string.Equals(route.Pattern, pattern, StringComparison.Ordinal))
            {
                return route;
            }
        }

        return null;
    }
}
