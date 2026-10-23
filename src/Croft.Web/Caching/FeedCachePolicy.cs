using Croft.Web.Options;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Primitives;

namespace Croft.Web.Caching;

/// <summary>Endpoint metadata that marks the feed and the sitemap: the cache pipeline reads it (the
/// named policy the endpoint carries is framework-internal and cannot be asked).</summary>
public sealed class FeedDocumentMetadata
{
    public static readonly FeedDocumentMetadata Instance = new();
}

/// <summary>
/// The output-cache policy of the feed and the sitemap (brief 14b item 2, .NET review M-18): the
/// same document for every caller, kept for <see cref="OutputCacheSettings.FeedDuration"/> and
/// tagged <see cref="ReadingPageCachePolicy.ContentTag"/> like the reading pages, so a staff write on
/// this pod drops it too. A crawler's fetch costs one build per duration; the endpoint handler sends
/// <c>Cache-Control: public, max-age=…</c> (see <see cref="CacheControlValue"/>) so browsers and
/// proxies keep it as long. Only a 200 without a cookie is stored.
/// </summary>
public sealed class FeedCachePolicy(OutputCacheSettings settings) : IOutputCachePolicy
{
    /// <summary>The name the policy is registered under, and the one the two endpoints use.</summary>
    public const string PolicyName = "Feed";

    /// <summary>The <c>Cache-Control</c> value of the feed and the sitemap: public, for as long as the server keeps them.</summary>
    public static string CacheControlValue(OutputCacheSettings settings) =>
        $"public, max-age={(long)settings.FeedDuration.TotalSeconds}";

    /// <summary>Whether this request is for the feed or the sitemap and may be cached.</summary>
    internal static bool IsCacheable(HttpContext context, OutputCacheSettings settings) =>
        settings.FeedCached && IsFeedDocument(context);

    /// <summary>Whether the request is for the feed or the sitemap: the request-shaped half of
    /// <see cref="IsCacheable"/>, which the response compression shares (<see cref="ReadingPageCompression"/>).</summary>
    internal static bool IsFeedDocument(HttpContext context) =>
        (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
        && context.GetEndpoint()?.Metadata.GetMetadata<FeedDocumentMetadata>() is not null;

    public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellation)
    {
        if (!IsCacheable(context.HttpContext, settings))
        {
            return ValueTask.CompletedTask;
        }

        context.EnableOutputCaching = true;
        context.AllowCacheLookup = true;
        context.AllowCacheStorage = true;
        context.AllowLocking = true;
        context.ResponseExpirationTimeSpan = settings.FeedDuration;
        context.Tags.Add(ReadingPageCachePolicy.ContentTag);
        ReadingPageCompression.VaryByEncoding(context);
        return ValueTask.CompletedTask;
    }

    public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellation) =>
        ValueTask.CompletedTask;

    public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellation)
    {
        var response = context.HttpContext.Response;
        if (response.StatusCode != StatusCodes.Status200OK || !StringValues.IsNullOrEmpty(response.Headers.SetCookie))
        {
            context.AllowCacheStorage = false;
        }

        return ValueTask.CompletedTask;
    }
}
