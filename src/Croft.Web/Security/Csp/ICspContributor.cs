namespace Croft.Web.Security.Csp;

/// <summary>
/// Adds sources to the CSP of the requests it cares about. A contributor decides from the request
/// alone, and the middleware runs it once per distinct <see cref="CacheKey"/>, not once per
/// request: the finished header string is kept and every request with the same key gets it.
/// </summary>
public interface ICspContributor
{
    void Contribute(HttpContext context, CspPolicy policy);

    /// <summary>
    /// The part of the request this contributor's output depends on, as a short string from a
    /// small set (a path prefix test, not the path): requests with the same key get the same
    /// sources, so the middleware builds their header once. Return an empty string when the
    /// output does not depend on the request. The default, <c>null</c>, says "I do not know":
    /// the middleware then runs every contributor for each request, as before the header was
    /// cached. A contributor that varies by request and answers anything else is a bug.
    /// </summary>
    string? CacheKey(HttpContext context) => null;
}
