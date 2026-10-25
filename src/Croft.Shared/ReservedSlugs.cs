using System.Collections.Frozen;

namespace Croft.Shared;

/// <summary>
/// The first path segments the Web app serves for itself: root endpoints (robots.txt, feed.xml,
/// …), every routable component's route root, the OIDC callbacks and the static-asset roots.
/// A page lives at /<c>slug</c>, so a page slug equal to any of these would be shadowed by the
/// Web app's own routes and static assets. The Web unit guard test derives every route and
/// mapped endpoint's first segment mechanically and asserts it appears here.
/// </summary>
public static class ReservedSlugs
{
    private static readonly FrozenSet<string> All =
    [
        with(StringComparer.OrdinalIgnoreCase),
        // Root endpoints and files the Web app serves (Program.cs).
        "robots.txt", "sitemap.xml", "feed.xml", "manifest.webmanifest", "branding",
        "login", "logout", "backchannel-logout", "csp-report",
        // The Mastodon reply form's landing page (/discussion/mastodon/reply, brief 12b).
        "discussion",
        // The discussion's avatar proxy (/social/avatar/{hash}.webp, brief 12b).
        "social",
        // The probes' shared root (/health/live, /health/ready).
        "health",
        // OIDC middleware callbacks.
        "signin-oidc", "signout-callback-oidc",
        // API proxy paths on the Web host.
        "api",
        // Blazor's own endpoints and the static-asset roots (wwwroot and framework/RCL assets).
        "_blazor", "_framework", "_content", "app.css", "favicon.ico", "components",
        "css", "js", "fonts", "lib", "branding-default", "croft.web.styles.css",
        // Routable components' first route segments (case-insensitive).
        "admin", "categories", "category", "editor", "error", "not-found", "post", "search", "tag",
    ];

    /// <summary>Whether <paramref name="slug"/> would shadow one of the Web app's own paths.</summary>
    public static bool Contains(string slug) => All.Contains(slug);
}
