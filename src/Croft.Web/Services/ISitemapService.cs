namespace Croft.Web.Services;

public interface ISitemapService
{
    /// <summary>The sitemap.xml document for the whole site, ready to serve.</summary>
    Task<string> BuildXmlAsync(CancellationToken ct = default);
}
