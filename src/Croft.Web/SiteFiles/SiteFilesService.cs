using System.Collections.Frozen;
using System.Security.Cryptography;

namespace Croft.Web;

/// <summary>
/// Reads the site's files folder (<c>Blog:SiteFilesPath</c>, default <c>/app/site-files</c>)
/// once at startup and builds an immutable <see cref="SiteFiles"/> snapshot for the endpoint to
/// serve. A missing or empty folder means no site files; an oversized, mistyped or shadowing
/// file fails startup with a <see cref="SiteFilesException"/>, naming the file and the rule.
/// </summary>
public static class SiteFilesService
{
    /// <summary>Hard upper bound on a site file's size: 64 KiB. Keeps a single served file small
    /// enough to read and hash at startup and small enough that a malicious configmap cannot
    /// exhaust the pod's memory. Measured against the largest well-known file (Apple's AASA
    /// JSON) and a comfortable margin for security.txt and WebFinger JSON.</summary>
    public const int MaxFileBytes = 64 * 1024;

    /// <summary>URL paths the app already serves at startup; any site file at one of them fails
    /// startup, naming both. Files with an extension or under <c>.well-known/</c> cannot collide
    /// with page slugs (<c>[a-z0-9-]+</c>), so no slug check is needed. Maintained alongside
    /// <c>Program.cs</c>: every hand-mapped endpoint that produces a public URL is here.
    /// <c>MapStaticAssets</c> assets are not listed (they come from the build): they are checked
    /// at startup against the real endpoint set by <see cref="EnsureNoStaticAssetShadowing"/>.</summary>
    private static readonly FrozenSet<string> ReservedPaths = new[]
    {
        "/robots.txt",
        "/sitemap.xml",
        "/feed.xml",
        "/manifest.webmanifest",
        "/favicon.ico",
        "/branding/theme.css",
        "/api/media",
        "/api/analytics/pageview",
        "/csp-report",
        "/health/live",
        "/health/ready",
        "/login",
        "/logout",
        "/backchannel-logout",
        "/signin-oidc",
        "/signout-callback-oidc",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reserved prefixes: a site file at <c>/branding/x.txt</c> shadows the branding
    /// endpoint and fails startup. <c>/api/</c> is reserved as a prefix because every API surface lives below it
    /// (and its concrete paths above name the ones the file would shadow outright).</summary>
    private static readonly FrozenSet<string> ReservedPrefixes = new[]
    {
        "/branding/", "/health/", "/api/",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The exact extensions a site file may carry, with the content-type each maps to.
    /// Site file names are case-sensitive (the name is the URL, matched exactly), so the tables
    /// here are ordinal on purpose: a case variant such as <c>Humans.TXT</c> is refused.
    /// Anything else fails startup, including a file with no extension — the file must be a
    /// well-known file by name (see <see cref="AllowedByPath"/>).</summary>
    private static readonly FrozenDictionary<string, string> AllowedExtensions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [".txt"] = "text/plain; charset=utf-8",
        [".json"] = "application/json",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The two well-known files with no extension that the site may publish. Both are
    /// served with an explicit content-type rather than letting the framework guess (or worse,
    /// letting a browser sniff). RFC 8615 reserves the <c>.well-known/</c> prefix for them.</summary>
    private static readonly FrozenDictionary<string, string> AllowedByPath = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [".well-known/apple-app-site-association"] = "application/json",
        // Brief 11a item 3: WebFinger (RFC 7033) is served as application/jrd+json — a JSON
        // Resource Descriptor. The content type differs from the .json extension's
        // application/json, so it's pinned here, alongside the AASA file.
        [".well-known/webfinger"] = "application/jrd+json",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Reads the folder and returns the immutable snapshot. A missing folder, an empty
    /// folder or one with no files (every entry a sub-folder) returns <see cref="SiteFiles.Empty"/>.</summary>
    /// <param name="folderPath">The site's folder; null or whitespace-empty means no site files.</param>
    public static SiteFiles Load(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return SiteFiles.Empty;
        }

        var byPath = new Dictionary<string, SiteFile>(StringComparer.Ordinal);
        foreach (var file in EnumerateFiles(folderPath))
        {
            var relative = Path.GetRelativePath(folderPath, file).Replace('\\', '/');
            var urlPath = "/" + relative;
            // First: the name becomes a route and a URL, so it must be plain (see ValidateName).
            ValidateName(relative);
            // Shadowing comes first: a file at /robots.txt (whatever its extension) is the more
            // useful failure, since the only way to fix it is to move the file — its extension
            // being wrong would just swap the error.
            ValidateReserved(urlPath);
            ValidateExtensionOrPath(relative);
            ValidateSize(file);

            var bytes = File.ReadAllBytes(file);
            var contentType = ResolveContentType(relative);
            var etag = "\"" + Convert.ToHexString(SHA256.HashData(bytes))[..32].ToLowerInvariant() + "\"";
            byPath[urlPath] = new SiteFile(relative, bytes, contentType, etag);
        }

        return new SiteFiles(byPath.ToFrozenDictionary(StringComparer.Ordinal));
    }

    /// <summary>Fails startup when a site file is at the URL of a static asset (<c>MapStaticAssets</c>,
    /// e.g. <c>wwwroot/fonts/OFL-*.txt</c>): the asset would win and the site file would be
    /// silently never served.</summary>
    /// <param name="siteFiles">The loaded snapshot.</param>
    /// <param name="staticAssetUrlPaths">The URL paths (leading <c>/</c>) the static assets are served at.</param>
    public static void EnsureNoStaticAssetShadowing(SiteFiles siteFiles, IEnumerable<string> staticAssetUrlPaths)
    {
        var assets = staticAssetUrlPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in siteFiles.ByPath.Values)
        {
            if (assets.Contains(file.UrlPath))
            {
                throw new SiteFilesException(
                    $"site file '{file.RelativePath}' is at '{file.UrlPath}', the URL of a static asset "
                    + "the app serves; the asset would win and the site file would never be served");
            }
        }
    }

    private static IEnumerable<string> EnumerateFiles(string folderPath)
    {
        // EnumerateFilesRecursive: walk the folder, return every regular file. Symlinks (a
        // Kubernetes ConfigMap mount's `..data/` entries) resolve like any other file.
        var stack = new Stack<string>();
        stack.Push(folderPath);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                // A Kubernetes ConfigMap mount holds ..data and ..<timestamp> next to the
                // projected paths, which are symlinks through them: the mirrors are not files.
                if (Path.GetFileName(entry).StartsWith("..", StringComparison.Ordinal))
                {
                    continue;
                }

                if (Directory.Exists(entry))
                {
                    stack.Push(entry);
                }
                else
                {
                    yield return entry;
                }
            }
        }
    }

    /// <summary>A site file's name is its URL and is matched exactly. The route is registered from
    /// the name, so a name such as <c>{id}.txt</c> must never be read as a route template (it
    /// would answer for every <c>/anything.txt</c>). Only letters, digits, <c>.</c> <c>-</c> <c>_</c>
    /// and the <c>/</c> between folders are accepted: nothing in that set is significant in a
    /// route template, and a stricter whitelist beats listing the dangerous characters.</summary>
    private static void ValidateName(string relative)
    {
        if (!relative.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '/'))
        {
            throw new SiteFilesException(
                $"site file '{relative}' has a name with characters outside the allowed set; "
                + "only letters, digits, '.', '-', '_' and '/' are allowed (the name is the URL, "
                + "and '{', '}', '*' and '?' would change what a route matches)");
        }
    }

    private static void ValidateExtensionOrPath(string relative)
    {
        var ext = Path.GetExtension(relative);
        if (ext.Length > 0)
        {
            if (!AllowedExtensions.ContainsKey(ext))
            {
                if (AllowedExtensions.ContainsKey(ext.ToLowerInvariant()))
                {
                    throw new SiteFilesException(
                        $"site file '{relative}' has the extension '{ext}'; site file names are "
                        + $"case-sensitive and the extension must be lowercase ('{ext.ToLowerInvariant()}')");
                }

                throw new SiteFilesException(
                    $"site file '{relative}' has a forbidden extension '{ext}'; "
                    + "only .txt and .json are allowed");
            }

            return;
        }

        if (!AllowedByPath.ContainsKey(relative))
        {
            var lowered = AllowedByPath.Keys.FirstOrDefault(k => string.Equals(k, relative, StringComparison.OrdinalIgnoreCase));
            if (lowered is not null)
            {
                throw new SiteFilesException(
                    $"site file '{relative}' is not a known well-known file name; site file names are "
                    + $"case-sensitive and must be lowercase ('{lowered}')");
            }

            throw new SiteFilesException(
                $"site file '{relative}' has no extension and is not a known well-known file; "
                + $"only {string.Join(", ", AllowedByPath.Keys)} are allowed without an extension");
        }
    }

    private static void ValidateReserved(string urlPath)
    {
        if (ReservedPaths.Contains(urlPath))
        {
            throw new SiteFilesException(
                $"site file at '{urlPath}' is reserved by the app and cannot be overridden");
        }

        foreach (var prefix in ReservedPrefixes)
        {
            if (urlPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new SiteFilesException(
                    $"site file at '{urlPath}' is under the reserved prefix '{prefix}' "
                    + "and cannot be overridden");
            }
        }
    }

    private static void ValidateSize(string filePath)
    {
        var info = new FileInfo(filePath);
        if (info.Length > MaxFileBytes)
        {
            throw new SiteFilesException(
                $"site file '{Path.GetFileName(filePath)}' is {info.Length} bytes, "
                + $"larger than the {MaxFileBytes}-byte limit");
        }
    }

    private static string ResolveContentType(string relative)
    {
        var ext = Path.GetExtension(relative);
        if (ext.Length > 0)
        {
            return AllowedExtensions[ext];
        }

        return AllowedByPath[relative];
    }
}