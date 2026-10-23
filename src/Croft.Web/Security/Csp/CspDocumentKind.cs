using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Components.Routing;

namespace Croft.Web.Security.Csp;

/// <summary>
/// What a response is, as far as its policy is concerned. A Razor component page is a document;
/// it is <see cref="Static"/> when it carries <c>[ExcludeFromInteractiveRouting]</c> (the seven
/// reading pages and, since brief 13a, the not-found and error pages: static SSR, no MudBlazor, no
/// circuit) and <see cref="Interactive"/> otherwise (admin and the editor: MudBlazor and a circuit). Everything else (a
/// static file, the feed, an API proxy, the /login redirect) is <see cref="NotADocument"/>.
/// </summary>
public enum CspDocumentKind
{
    NotADocument,
    Static,
    Interactive,
}

public static class CspDocument
{
    /// <summary>
    /// The kind of the response for <paramref name="context"/>. Only meaningful once routing has
    /// chosen the endpoint, which is after the CSP middleware has started (see
    /// <see cref="IDocumentCspContributor"/>): the same switch App.razor uses to pick the render
    /// mode, so the policy and the markup cannot disagree about which kind of page this is.
    /// </summary>
    public static CspDocumentKind KindOf(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<RootComponentMetadata>() is null)
        {
            return CspDocumentKind.NotADocument;
        }

        return context.AcceptsInteractiveRouting() ? CspDocumentKind.Interactive : CspDocumentKind.Static;
    }
}
