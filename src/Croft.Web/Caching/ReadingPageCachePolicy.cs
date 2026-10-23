using System.Globalization;
using Croft.Web.Options;
using Croft.Web.Security.Csp;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Primitives;

namespace Croft.Web.Caching;

/// <summary>
/// The output-cache policy of the reading pages (brief 14b). A response is cached only when it is
/// a <c>GET</c> or <c>HEAD</c> of one of <see cref="ReadingPages.Routes"/> from a request that is not
/// authenticated, the document is a static one (<see cref="CspDocumentKind.Static"/>, the same test
/// the antiforgery workaround W-092 uses), and the answer is a 200 without a <c>Set-Cookie</c>.
/// Every entry is tagged <see cref="ContentTag"/>, which the staff write paths evict, and lives for
/// <see cref="OutputCacheSettings.Duration"/>. The key is the path plus the query parameters the page
/// reads (and the host and scheme are the same for all readers, see <see cref="ReadingPageCache"/>):
/// a query string the page does not read mints no entry.
/// </summary>
public sealed class ReadingPageCachePolicy(OutputCacheSettings settings) : IOutputCachePolicy
{
    /// <summary>The tag on every cached reading page.</summary>
    public const string ContentTag = "content";

    /// <summary>The name the policy is registered under.</summary>
    public const string PolicyName = "ReadingPages";

    /// <summary>Whether this request may be answered from, and its answer stored in, the cache.</summary>
    internal static bool IsCacheable(HttpContext context, OutputCacheSettings settings, out ReadingPageRoute? route)
    {
        route = null;
        return settings.PagesCached && IsPublicDocument(context, out route);
    }

    /// <summary>
    /// Whether the request is for a reading page and from a reader who is not signed in: the
    /// request-shaped half of <see cref="IsCacheable"/> (the settings decide whether the cache is on).
    /// The response compression takes the same test, so what it compresses is what the cache may
    /// store (<see cref="ReadingPageCompression"/>).
    /// </summary>
    internal static bool IsPublicDocument(HttpContext context, out ReadingPageRoute? route)
    {
        route = null;
        var request = context.Request;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            return false;
        }

        // A signed-in reader (or a bearer token) gets their own render, never a shared one.
        if (context.User.Identity?.IsAuthenticated == true || !StringValues.IsNullOrEmpty(request.Headers.Authorization))
        {
            return false;
        }

        if (CspDocument.KindOf(context) is not CspDocumentKind.Static)
        {
            return false;
        }

        route = ReadingPages.Match(context);
        return route is not null;
    }

    public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellation)
    {
        if (!IsCacheable(context.HttpContext, settings, out var route))
        {
            return ValueTask.CompletedTask;
        }

        context.EnableOutputCaching = true;
        context.AllowCacheLookup = true;
        context.AllowCacheStorage = true;
        context.AllowLocking = true;
        context.ResponseExpirationTimeSpan = settings.Duration;
        context.Tags.Add(ContentTag);
        ReadingPageCompression.VaryByEncoding(context);

        var query = context.HttpContext.Request.Query;
        foreach (var key in route!.QueryKeys)
        {
            // The page number is what the page reads (PageQuery): ?page=abc, ?page=0 and ?page=1 are
            // all page 1, so they are one entry.
            context.CacheVaryByRules.VaryByValues[key] = key == ReadingPages.PageKey
                ? PageQuery.ReadPageNumber(context.HttpContext.Request.QueryString.Value).ToString(CultureInfo.InvariantCulture)
                : query[key].ToString();
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellation) =>
        ValueTask.CompletedTask;

    public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellation)
    {
        var response = context.HttpContext.Response;

        // Only a 200 is shared (a 404 is the API's negative cache's business), and never one that
        // sets a cookie: a rejected session's sign-out is for that one reader.
        if (response.StatusCode != StatusCodes.Status200OK || !StringValues.IsNullOrEmpty(response.Headers.SetCookie))
        {
            context.AllowCacheStorage = false;
        }

        return ValueTask.CompletedTask;
    }
}
