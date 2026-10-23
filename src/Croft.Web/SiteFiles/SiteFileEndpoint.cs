namespace Croft.Web;

/// <summary>Marker metadata on the GET/HEAD endpoint for a single site file (security.txt,
/// WebFinger, Apple's AASA and similar). The rate limiter's exempt list keys on this metadata
/// so site files don't count against the page concurrency limit (they are cached, cheap and hot).</summary>
public sealed class SiteFileEndpoint
{
    /// <summary>The answer to any method but GET and HEAD: 405 with <c>Allow: GET, HEAD</c>.</summary>
    public static IResult MethodNotAllowed(HttpContext context)
    {
        context.Response.Headers.Allow = "GET, HEAD";
        return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
    }
}