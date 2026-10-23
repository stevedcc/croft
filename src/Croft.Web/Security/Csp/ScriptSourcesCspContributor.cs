using System.Security.Cryptography;
using System.Text;
using Croft.Web.Authentication;
using Croft.Web.Services;
using Croft.Web.Options;
using Microsoft.Extensions.Options;

namespace Croft.Web.Security.Csp;

/// <summary>
/// The script-src of each kind of response. There is no <c>'self'</c> and no <c>strict-dynamic</c>
/// (a spike in Chromium and Firefox showed hash-allowed modules cannot import other modules, see
/// the 10c item 0 commit): a script runs only if the document names the directory it is served from,
/// absolute with the site's origin, or is the one inline script, allowed by its hash.
/// <list type="bullet">
/// <item>A static reading page: the theme bootstrap's hash, <c>/_framework/</c> (blazor.web.js),
/// <c>/js/</c> (the page scripts and the modules they import) and <c>/lib/</c> (highlight.js).</item>
/// <item>An interactive document: the same, plus <c>/_content/MudBlazor/</c> and
/// <c>/Components/Layout/</c> (the reconnect modal's module script).</item>
/// <item>The sign-out interstitial (not a Razor page): exactly <c>/js/logout.js</c>.</item>
/// <item>Anything else keeps the base policy's <c>script-src 'none'</c>.</item>
/// </list>
/// Every <c>&lt;script src&gt;</c> also carries the integrity digest MapStaticAssets computed (see
/// <see cref="StaticAssetIntegrity"/>), so even a file under an allowed directory must be the file the
/// build produced. The sources depend on the configuration only (hashes and a fixed origin), never
/// on the request, so the policy of a document kind is the same on every request and a cached
/// response stays valid; nonces would not allow that and are not used.
/// </summary>
public sealed class ScriptSourcesCspContributor(IOptions<BlogOptions> blogOptions) : IDocumentCspContributor
{
    private static readonly string ThemeBootstrapSource =
        $"'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(ThemeBootstrapScript.Text)))}'";

    private static readonly string[] StaticDirectories = ["/_framework/", "/js/", "/lib/"];
    private static readonly string[] InteractiveDirectories = ["/_content/MudBlazor/", "/Components/Layout/"];

    private readonly string? _configuredOrigin = OriginOf(blogOptions.Value.BaseUrl);

    public void Contribute(HttpContext context, CspPolicy policy, CspPolicy reportOnlyPolicy, CspDocumentKind kind)
    {
        // Blog:BaseUrl is required outside Development; there the request's own origin stands in,
        // so a local run without it still works.
        var origin = _configuredOrigin ?? $"{context.Request.Scheme}://{context.Request.Host}";
        switch (kind)
        {
            case CspDocumentKind.Static:
                policy.Add("script-src", [ThemeBootstrapSource, .. StaticDirectories.Select(path => origin + path)]);
                break;
            case CspDocumentKind.Interactive:
                policy.Add("script-src", [ThemeBootstrapSource,
                    .. StaticDirectories.Select(path => origin + path),
                    .. InteractiveDirectories.Select(path => origin + path)]);
                break;
            default:
                if (context.Request.Path == SignOutInterstitial.Path)
                {
                    policy.Add("script-src", origin + "/js/logout.js");
                }

                break;
        }
    }

    /// <summary>What the sources depend on besides the kind: the site's configured origin (nothing
    /// to add), or the request's own origin where none is configured (a local run), and for a
    /// response that is no document, whether it is the sign-out interstitial.</summary>
    public string? CacheKey(HttpContext context, CspDocumentKind kind)
    {
        var uses = kind is CspDocumentKind.Static or CspDocumentKind.Interactive
            ? ""
            : context.Request.Path == SignOutInterstitial.Path ? "signout" : null;
        if (uses is null)
        {
            // Nothing is added for the response (script-src stays 'none').
            return "";
        }

        return _configuredOrigin is null ? $"{uses}@{context.Request.Scheme}://{context.Request.Host}" : uses;
    }

    private static string? OriginOf(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : null;
}
