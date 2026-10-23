using Croft.Web.Options;

namespace Croft.Web.Caching;

/// <summary>
/// Wires the reading pages', the feed's and the images' output cache into the pipeline (brief 14b), around the framework's
/// <c>UseOutputCache</c>: two small adaptations for what the framework does with the request's
/// host and scheme and with a header value. Both are registered workarounds (W-098, W-099).
/// </summary>
public static class ReadingPageCache
{
    // WORKAROUND(W-099, ASP.NET Core): the output cache's key includes the request's scheme and host, so the key is built from stand-ins and the request's own values are put back for the render
    /// <summary>Stands in for the host while the key is built; never reaches a page.</summary>
    private static readonly HostString KeyHost = new("reading-pages.cache.invalid");

    /// <summary>Stands in for the scheme while the key is built; never reaches a page.</summary>
    private const string KeyScheme = "https";
    // END WORKAROUND(W-099)

    // WORKAROUND(W-098, ASP.NET Core): the output cache's serializer returns the enhanced-navigation header's value "allow" as "Allow", which blazor.web.js does not accept
    /// <summary>The header Blazor sets on a page it renders so its client may patch the page in.
    /// The client compares the value to <c>allow</c>, case-sensitively, and falls back to a full
    /// page load for anything else.</summary>
    private const string EnhancedNavigationHeader = "blazor-enhanced-nav";
    // END WORKAROUND(W-098)

    private static readonly object OriginalKey = new();

    public static IApplicationBuilder UseReadingPageCache(this IApplicationBuilder app, OutputCacheSettings settings)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Before the cache: settle the response encoding (it is part of the cache key).
        app.UseReadingPageCompressionNegotiation();

        // Before the cache.
        app.Use(async (context, next) =>
        {
            if (!ReadingPageCachePolicy.IsCacheable(context, settings, out _)
                && !FeedCachePolicy.IsCacheable(context, settings)
                && !MediaCachePolicy.IsCacheable(context, settings))
            {
                await next(context);
                return;
            }

            // WORKAROUND(W-098, ASP.NET Core): the output cache's serializer returns the enhanced-navigation header's value "allow" as "Allow", which blazor.web.js does not accept
            context.Response.OnStarting(static state =>
            {
                var headers = ((HttpContext)state).Response.Headers;
                if (headers.TryGetValue(EnhancedNavigationHeader, out var value) && value == "Allow")
                {
                    headers[EnhancedNavigationHeader] = "allow";
                }

                return Task.CompletedTask;
            }, context);
            // END WORKAROUND(W-098)

            // WORKAROUND(W-099, ASP.NET Core): the output cache's key includes the request's scheme and host, so the key is built from stand-ins and the request's own values are put back for the render
            // A reading page is the same for every host and scheme, so the key is built from the same
            // stand-ins for all of them. Without this the warm-up (127.0.0.1 over http) would fill an
            // entry no reader ever asks for, and a request with a Host header of its own would mint one.
            // The render (after the cache, below) and a cache hit (the finally) see the request as it came.
            var request = context.Request;
            var (host, scheme) = (request.Host, request.Scheme);
            (request.Host, request.Scheme) = (KeyHost, KeyScheme);
            context.Items[OriginalKey] = (host, scheme);
            // Callbacks run last-registered first: this one puts the request back before the CSP and
            // security-header callbacks (registered earlier, outside) read it when a cache hit starts the response.
            context.Response.OnStarting(static state =>
            {
                RestoreRequest((HttpContext)state);
                return Task.CompletedTask;
            }, context);
            try
            {
                await next(context);
            }
            finally
            {
                RestoreRequest(context);
            }

            // END WORKAROUND(W-099)
        });

        app.UseOutputCache();

        // After the cache, so the cache stores the compressed bytes and a hit compresses nothing.
        app.UseResponseCompression();

        // After the cache: reached only when the page must be rendered.
        app.Use(static async (context, next) =>
        {
            // WORKAROUND(W-099, ASP.NET Core): the output cache's key includes the request's scheme and host, so the key is built from stand-ins and the request's own values are put back for the render
            RestoreRequest(context);

            // END WORKAROUND(W-099)
            await next(context);
        });
        return app;
    }

    // WORKAROUND(W-099, ASP.NET Core): the output cache's key includes the request's scheme and host, so the key is built from stand-ins and the request's own values are put back for the render
    private static void RestoreRequest(HttpContext context)
    {
        if (context.Items.Remove(OriginalKey, out var original))
        {
            var (host, scheme) = ((HostString, string))original!;
            context.Request.Host = host;
            context.Request.Scheme = scheme;
        }
    }
    // END WORKAROUND(W-099)
}
