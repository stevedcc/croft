// WORKAROUND-FILE(W-091, Blazor): blazor.web.js (enhanced navigation) violates require-trusted-types-for on every reading page, so the directives are reported and not enforced
namespace Croft.Web.Security.Csp;

/// <summary>
/// Trusted Types for the static reading pages: <c>require-trusted-types-for 'script'</c> with
/// <c>trusted-types 'none'</c> (no policy may be created, and no pass-through default policy
/// exists), shipped in <c>Content-Security-Policy-Report-Only</c> and reporting to the same
/// <c>/csp-report</c> endpoint as the enforced policy. The 10c measurement (all three engines)
/// found two kinds of sink: the site's own (highlight.js's innerHTML, removed from post.js) and
/// blazor.web.js's: its enhanced navigation parses the fetched document with DOMParser (and
/// Firefox also re-creates the JSON-LD script element). Enforced, every enhanced navigation would
/// be blocked, so the reports show what remains until blazor.web.js is clean (or the reading
/// pages stop using it). Interactive documents are not measured and carry no Trusted Types policy.
/// </summary>
public sealed class TrustedTypesCspContributor : IDocumentCspContributor
{
    public void Contribute(HttpContext context, CspPolicy policy, CspPolicy reportOnlyPolicy, CspDocumentKind kind)
    {
        if (kind is not CspDocumentKind.Static)
        {
            return;
        }

        reportOnlyPolicy
            .Add("require-trusted-types-for", "'script'")
            .Add("trusted-types", "'none'")
            .Add("report-uri", "/csp-report")
            .Add("report-to", "csp");
    }

    public string? CacheKey(HttpContext context, CspDocumentKind kind) => "";
}
