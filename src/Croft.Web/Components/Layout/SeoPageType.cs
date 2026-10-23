namespace Croft.Web.Components.Layout;

/// <summary>The kind of page an SEO head describes: drives <c>og:type</c> and which JSON-LD
/// entity is emitted — <c>BlogPosting</c> for articles, a schema.org <c>WebPage</c> for the
/// root-level static pages (dated with their publish and content-edit times), and the site's
/// <c>Blog</c> for listing pages.</summary>
public enum SeoPageType
{
    Article,
    Website,
    Page,
}
