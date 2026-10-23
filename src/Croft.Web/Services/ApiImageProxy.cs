using System.Net.Http.Headers;

namespace Croft.Web.Services;

/// <summary>
/// The Web's image proxies (<c>/api/media/{id}</c>, brief 14c, and <c>/social/avatar/{hash}.webp</c>, brief 12b):
/// the browser asks the Web app, the Web app asks the API and streams the answer back.
/// </summary>
internal static class ApiImageProxy
{
    /// <summary>Asks the API for <paramref name="apiPath"/> and copies the answer to the response.</summary>
    /// <param name="context">The browser's request; the answer is written to its response.</param>
    /// <param name="httpClientFactory">Makes the "BlogApi" client.</param>
    /// <param name="configuration">Where the ingest key comes from.</param>
    /// <param name="apiPath">The API route to ask.</param>
    /// <param name="logKey">What the Warning for an unreachable API names (the media id or the avatar hash).</param>
    /// <param name="token">The reader's access token, or null for an anonymous read.</param>
    /// <param name="ct">Cancelled when the browser goes away.</param>
    public static async Task ForwardAsync(
        HttpContext context, IHttpClientFactory httpClientFactory, IConfiguration configuration,
        string apiPath, string logKey, string? token, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("BlogApi");
        using var request = new HttpRequestMessage(HttpMethod.Get, apiPath);
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        // The API's per-client limit counts the visitor, not this pod: it honours X-Visitor-IP only
        // beside the ingest key (the same pairing the page-view beacon uses).
        ForwardVisitor(request, context, configuration);

        // Disposed when the copy ends or fails (a reader aborted by the minimum data rate), so a
        // slow reader releases the API connection and its permit with its own.
        // The resilience policy's timeout covers getting the headers here, not the body stream below.
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || ApiUnavailableException.IsResilienceFailure(ex))
        {
            // An image request gets a bare 503, not the HTML error page.
            ApiOutageLog.MediaUnavailable(
                context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Croft.Web.MediaProxy"),
                logKey, ex.GetType().Name);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        using var disposeResponse = response;
        context.Response.StatusCode = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode)
            return;

        var cacheControl = response.Headers.CacheControl?.ToString();
        if (!string.IsNullOrEmpty(cacheControl))
        {
            context.Response.Headers.CacheControl = cacheControl;
        }

        context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
        context.Response.ContentLength = response.Content.Headers.ContentLength;

        await response.Content.CopyToAsync(context.Response.Body, ct);
    }

    /// <summary>The visitor's address and the ingest key that makes the API believe it.</summary>
    public static void ForwardVisitor(HttpRequestMessage request, HttpContext context, IConfiguration configuration)
    {
        var visitorIp = context.Connection.RemoteIpAddress?.ToString();
        if (!string.IsNullOrEmpty(visitorIp))
        {
            request.Headers.Add("X-Visitor-IP", visitorIp);
        }

        var ingestKey = configuration["Analytics:IngestKey"];
        if (!string.IsNullOrEmpty(ingestKey))
        {
            request.Headers.Add("X-Analytics-Key", ingestKey);
        }
    }
}

/// <summary>The shape of an avatar hash: the 64 lower-case hex characters of a SHA-256.</summary>
internal static class SocialAvatarHash
{
    public static bool IsValid(string? hash) =>
        hash is { Length: 64 } && hash.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
