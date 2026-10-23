namespace Croft.Web.Security.Csp;

/// <summary>
/// Adds sources to the policy of a response according to the kind of document it is. The kind
/// comes from the endpoint routing chose, which does not exist yet when the CSP middleware
/// starts (it sits at the head of the pipeline so that even error responses carry a policy), so
/// these contributors run when the response is about to start (<c>OnStarting</c>) and the
/// header follows the policy until then. <c>reportOnlyPolicy</c> is the response's second, report-only
/// policy (<see cref="ICspFeature.ReportOnlyPolicy"/>), for directives that are reported, not enforced. An <see cref="ICspContributor"/> still decides from the
/// request alone, at the start. Like those, a document contributor runs once per distinct
/// (kind, <see cref="CacheKey"/>), not once per response.
/// </summary>
public interface IDocumentCspContributor
{
    void Contribute(HttpContext context, CspPolicy policy, CspPolicy reportOnlyPolicy, CspDocumentKind kind);

    /// <summary>
    /// The part of the request, beside <paramref name="kind"/>, this contributor's output depends
    /// on: see <see cref="ICspContributor.CacheKey"/>. An empty string means "the kind is all".
    /// <c>null</c> (the default) means the middleware runs every contributor for each response.
    /// </summary>
    string? CacheKey(HttpContext context, CspDocumentKind kind) => null;
}
