// WORKAROUND-FILE(W-092, Blazor): Razor components persist the antiforgery token on every page, so a static reading page set a cookie and no-store headers; IAntiforgery is wrapped to issue none there
using Microsoft.AspNetCore.Antiforgery;
using Croft.Web.Security.Csp;

namespace Croft.Web.Security;

/// <summary>
/// The antiforgery service, except that a static reading page never gets a token. Blazor's
/// prerender persists <c>AntiforgeryStateProvider.CurrentToken</c> for every Razor component
/// endpoint, form or no form, and generating it stores the cookie and adds
/// <c>Cache-Control: no-cache, no-store</c> and <c>Pragma: no-cache</c>. The reading pages render no
/// form (the search form is a GET), so the token protects nothing there; a reader must get no
/// cookie and a page that may be cached. <c>[RequireAntiforgeryToken(false)]</c> on the page does
/// not stop the generation (tried, 10c item 5). Interactive documents (admin, the editor,
/// the staff pages; /login is a redirect, not a document) and every POST keep the framework's behaviour untouched.
/// </summary>
public sealed class StaticDocumentAntiforgery(IAntiforgery inner) : IAntiforgery
{
    public AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext) =>
        CspDocument.KindOf(httpContext) is CspDocumentKind.Static
            ? new AntiforgeryTokenSet(requestToken: null, cookieToken: null, formFieldName: "__RequestVerificationToken", headerName: null)
            : inner.GetAndStoreTokens(httpContext);

    public AntiforgeryTokenSet GetTokens(HttpContext httpContext) => inner.GetTokens(httpContext);

    public Task<bool> IsRequestValidAsync(HttpContext httpContext) => inner.IsRequestValidAsync(httpContext);

    public Task ValidateRequestAsync(HttpContext httpContext) => inner.ValidateRequestAsync(httpContext);

    public void SetCookieTokenAndHeader(HttpContext httpContext) => inner.SetCookieTokenAndHeader(httpContext);
}
