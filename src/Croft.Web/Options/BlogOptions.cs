namespace Croft.Web.Options;

/// <summary>Blog settings bound from the "Blog" configuration section.</summary>
public class BlogOptions
{
    public const string SectionName = "Blog";

    /// <summary>The folder the site's branding lives in (branding.json and the image files).
    /// A missing folder or file means the built-in defaults.</summary>
    public string BrandingPath { get; set; } = "/app/branding";

    /// <summary>The folder of well-known files the site publishes (security.txt, WebFinger,
    /// Apple's AASA and similar). Each file becomes a public URL at its own path relative to
    /// the folder. A missing folder means no site files.</summary>
    public string SiteFilesPath { get; set; } = "/app/site-files";

    /// <summary>The public origin absolute URLs are built from (canonical, og:image, JSON-LD) —
    /// never the request's host. Required outside Development.</summary>
    public string BaseUrl { get; set; } = string.Empty;
}
