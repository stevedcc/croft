// WORKAROUND-FILE(W-007, MudBlazor): MudBlazor writes inline style attributes, so only /admin and /editor documents get style-src-attr unsafe-inline
namespace Croft.Web.Security.Csp;

/// <summary>
/// Allows inline style attributes on the staff half of the site — the pages under /admin and
/// /editor, where MudBlazor's grids, dialogs and tabs position cells, rows and overlays with
/// style attributes. Every other path (the public pages) keeps the base policy's
/// style-src-attr 'none': the public markup writes no style attribute at all.
///
/// The policy is per document, so the decision is the document request's path (see
/// <see cref="AdminArea"/>): a request under /admin or /editor is exactly a staff page's
/// document, and nothing else may loosen the ban.
/// </summary>
public sealed class AdminAreaCspContributor : ICspContributor
{
    public void Contribute(HttpContext context, CspPolicy policy)
    {
        if (AdminArea.ContainsPath(context.Request.Path))
        {
            policy.Add("style-src-attr", "'unsafe-inline'");
        }
    }

    public string? CacheKey(HttpContext context) => AdminArea.ContainsPath(context.Request.Path) ? "admin" : "";
}
