// WORKAROUND-FILE(W-007, Blazor): links crossing the staff boundary opt out of enhanced navigation and router interception so the CSP of the new document applies

namespace Croft.Web;

/// <summary>
/// The staff half of the site — the pages under /admin and /editor — and the boundary it forms
/// with the public pages. The Content-Security-Policy is fixed per document (enhanced navigation
/// keeps the policy of the first document), and the two halves carry different policies: the
/// staff documents allow inline style attributes (MudBlazor's grids, dialogs and tabs write
/// them), the public ones ban them. Two consequences live here: which documents get the
/// loosened policy (AdminAreaCspContributor), and which links must load a fresh document — every
/// link that crosses the boundary, so the policy is replaced with the content it belongs to.
/// </summary>
public static class AdminArea
{
    /// <summary>True for a request path that leads to a staff document (a page under /admin or
    /// /editor). StartsWithSegments keeps /administer and /editorial on the public side.</summary>
    public static bool ContainsPath(PathString path) =>
        path.StartsWithSegments("/admin") || path.StartsWithSegments("/editor");

    /// <summary>The attributes for a link: every link that crosses the boundary carries
    /// <c>data-enhance-nav="false"</c> (Blazor's enhanced navigation skips it) and
    /// <c>target="_top"</c> (the interactive router skips links whose target is not _self) —
    /// together they make the browser load the target as a fresh document with its own policy.
    /// Links inside one area get no attributes and keep enhanced navigation.</summary>
    public static IReadOnlyDictionary<string, object?> LinkAttributes(bool inAdminArea, string href) =>
        ContainsPath(PathOf(href)) != inAdminArea ? FullLoad : Enhanced;

    private static readonly IReadOnlyDictionary<string, object?> FullLoad =
        new Dictionary<string, object?> { ["data-enhance-nav"] = "false", ["target"] = "_top" };

    private static readonly IReadOnlyDictionary<string, object?> Enhanced =
        new Dictionary<string, object?>();

    /// <summary>The link target's path, without query or fragment.</summary>
    private static PathString PathOf(string href)
    {
        var end = href.IndexOfAny(['?', '#']);
        return new PathString(end < 0 ? href : href[..end]);
    }
}
