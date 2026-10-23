namespace Croft.Web;

/// <summary>
/// A single site file the site-files folder supplies, read once at startup and kept in memory
/// for the lifetime of the process: the exact bytes, the content-type it must be served with,
/// and the strong ETag the file's SHA-256 prefix hashes to. A Kubernetes ConfigMap rolls the
/// pods when the folder changes, so the process never re-reads after startup.
/// </summary>
public sealed record SiteFile(string RelativePath, byte[] Bytes, string ContentType, string ETag)
{
    /// <summary>The URL path the file is served under: leading <c>/</c> plus the relative path,
    /// forward-slashes (the folder's separator on every supported OS).</summary>
    public string UrlPath => "/" + RelativePath.Replace('\\', '/');
}

/// <summary>Every site file the folder holds: an immutable snapshot built at startup, looked up by
/// the file's URL path. An empty folder (or a missing one) gives an empty site, with no errors
/// and no files; a folder that fails validation (bad extension, oversized, shadowing) throws
/// <see cref="SiteFilesException"/> with a message naming the file and the rule.</summary>
public sealed class SiteFiles(IReadOnlyDictionary<string, SiteFile> byPath)
{
    /// <summary>Every served file, keyed by its URL path (leading <c>/</c>).</summary>
    public IReadOnlyDictionary<string, SiteFile> ByPath { get; } = byPath;

    /// <summary>No folder configured or the folder is empty: nothing to serve.</summary>
    public static SiteFiles Empty { get; } = new(new Dictionary<string, SiteFile>());

    /// <summary>The site file served at <paramref name="urlPath"/> (leading <c>/</c>); null if
    /// the folder holds no file at that path.</summary>
    public SiteFile? Resolve(string urlPath) => ByPath.GetValueOrDefault(urlPath);
}