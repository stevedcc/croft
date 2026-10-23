using Croft.Web.Options;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Croft.Web.Caching;

/// <summary>Endpoint metadata that marks the image proxies (<c>/api/media/{id}</c> and, brief 12b, <c>/social/avatar/{hash}.webp</c>): the cache pipeline reads it
/// (the named policy the endpoint carries is framework-internal and cannot be asked).</summary>
public sealed class MediaDocumentMetadata
{
    public static readonly MediaDocumentMetadata Instance = new();
}

/// <summary>
/// The output-cache policy of the image proxy <c>GET /api/media/{id}</c> (brief 14c item 6): an image
/// the API calls public is kept in this pod's memory, so the second visitor's request does not cost
/// the API a database lookup and a file read. The same policy serves the discussion's avatars (brief 12b, no change
/// needed: the API says <c>public, max-age=31536000, immutable</c>). The API's own <c>Cache-Control</c> decides what is
/// shared, and it is replayed with the image.
/// <list type="bullet">
/// <item>Only an anonymous request is answered from, and filled into, the cache: staff see drafts, so
/// a signed-in request always goes to the API with its own token.</item>
/// <item>Only a 200 whose <c>Cache-Control</c> says <c>public</c> is stored: a draft's image
/// (<c>no-store</c>), any other status and the proxy's own 503 never are.</item>
/// <item>An image above <see cref="OutputCacheSettings.MediaMaximumBodyKilobytes"/>, or one whose
/// length the API did not say, is streamed whole and not stored.</item>
/// <item>The key is the path alone (the query string is not part of it), the entry is tagged
/// <see cref="ReadingPageCachePolicy.ContentTag"/> so every staff write (a media delete included) drops
/// it, and it lives for <see cref="OutputCacheSettings.Duration"/>, the pages' minute: an unpublished
/// post's images are gone from every pod within that minute.</item>
/// </list>
/// </summary>
public sealed class MediaCachePolicy(OutputCacheSettings settings) : IOutputCachePolicy
{
    /// <summary>The name the policy is registered under, and the one the proxy endpoint uses.</summary>
    public const string PolicyName = "Media";

    /// <summary>Whether this request is for an image and may be answered from, and its answer stored in, the cache.</summary>
    internal static bool IsCacheable(HttpContext context, OutputCacheSettings settings)
    {
        if (!settings.PagesCached
            || (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
            || context.GetEndpoint()?.Metadata.GetMetadata<MediaDocumentMetadata>() is null)
        {
            return false;
        }

        // A signed-in reader (or a bearer token) may see a draft's image: never the shared copy.
        return context.User.Identity?.IsAuthenticated != true && StringValues.IsNullOrEmpty(context.Request.Headers.Authorization);
    }

    public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellation)
    {
        if (!IsCacheable(context.HttpContext, settings))
        {
            return ValueTask.CompletedTask;
        }

        // No vary rules: the key is the path (an image is the same for every host, scheme and query).
        // No locking, unlike the pages: concurrent first requests for one image each ask the API, as
        // they always did, instead of queueing behind the first one (a slow or failing first request
        // would hold every other reader of that image). The API's per-client limit counts those requests
        // (ClientRequestLimitTests pins that a client's concurrent image requests all reach the API).
        context.EnableOutputCaching = true;
        context.AllowCacheLookup = true;
        context.AllowCacheStorage = true;
        context.AllowLocking = false;
        context.ResponseExpirationTimeSpan = settings.Duration;
        context.Tags.Add(ReadingPageCachePolicy.ContentTag);
        return ValueTask.CompletedTask;
    }

    public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellation) =>
        ValueTask.CompletedTask;

    public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellation)
    {
        var response = context.HttpContext.Response;
        var isPublic = CacheControlHeaderValue.TryParse(response.Headers.CacheControl.ToString(), out var cacheControl) && cacheControl.Public;
        var tooBig = response.ContentLength is not { } length || length > settings.MediaMaximumBodyKilobytes * 1024L;
        if (response.StatusCode != StatusCodes.Status200OK || !isPublic || tooBig
            || !StringValues.IsNullOrEmpty(response.Headers.SetCookie))
        {
            context.AllowCacheStorage = false;
        }

        return ValueTask.CompletedTask;
    }
}
