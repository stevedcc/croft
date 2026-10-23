namespace Croft.Web.Services;

public interface IFeedService
{
    /// <summary>The feed.xml Atom 1.0 document for the whole site, ready to serve.</summary>
    Task<string> BuildXmlAsync(CancellationToken ct = default);
}
