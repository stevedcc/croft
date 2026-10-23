namespace Croft.Api.Social;

/// <summary>Which URLs from a remote platform may be linked or downloaded: absolute http(s) (https for downloads), with a host and no credentials.</summary>
internal static class SocialUrl
{
    /// <summary>The normalised URL when <paramref name="url"/> is an absolute http or https URL with a host and no user info; otherwise null.</summary>
    public static string? SafeHttp(string? url) => Safe(url, httpsOnly: false);

    /// <summary>As <see cref="SafeHttp"/>, https only (what may be downloaded).</summary>
    public static string? SafeHttps(string? url) => Safe(url, httpsOnly: true);

    private static string? Safe(string? url, bool httpsOnly)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return null;

        var schemeOk = uri.Scheme == Uri.UriSchemeHttps || (!httpsOnly && uri.Scheme == Uri.UriSchemeHttp);
        return schemeOk && !string.IsNullOrEmpty(uri.Host) && string.IsNullOrEmpty(uri.UserInfo) ? uri.AbsoluteUri : null;
    }
}
