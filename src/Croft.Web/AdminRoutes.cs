using Croft.Shared.Enums;

namespace Croft.Web;

/// <summary>The staff editor's addresses. One component edits both kinds, but a page lives under
/// /admin/pages and a post under /admin/posts, so the address, the sidebar highlight and the way
/// back to the right list follow the kind.</summary>
public static class AdminRoutes
{
    /// <summary>The edit route of an existing post or page.</summary>
    public static string Edit(PostKind kind, Guid id) =>
        kind == PostKind.Page ? $"/admin/pages/{id}/edit" : $"/admin/posts/{id}/edit";
}
