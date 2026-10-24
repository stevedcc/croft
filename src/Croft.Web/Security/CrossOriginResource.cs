namespace Croft.Web.Security;

/// <summary>
/// Endpoint metadata for a response other sites and browser-based readers may load as a
/// subresource: the media images (shown by feed readers), the feed, the sitemap, robots.txt,
/// the well-known files, the manifest and the branding images. SecurityHeadersMiddleware sends
/// <c>Cross-Origin-Resource-Policy: cross-origin</c> on these and <c>same-origin</c> on everything
/// else, which only this site's own documents load.
/// </summary>
public sealed class CrossOriginResource
{
    public static readonly CrossOriginResource Instance = new();

    private CrossOriginResource()
    {
    }
}
