// WORKAROUND-FILE(W-019, MudBlazor): the provider always emits a chart style block (no switch), hashed beside the theme blocks
using System.Security.Cryptography;
using System.Text;
using MudBlazor;

namespace Croft.Web.Security.Csp;

/// <summary>
/// Allows MudThemeProvider's inline style blocks by their hashes: every interactive document's
/// style-src-elem carries 'sha256-…' of each block's text, taken exactly as the browser's DOM
/// holds it (see <see cref="MudThemeProviderCss"/>), for both theme modes. Both modes go on
/// every response because an interactive theme switch replaces the block's text in the DOM
/// without any new response — the hash of the mode the switch lands on must already be in the
/// header the page loaded with.
///
/// The blocks are the theme variables and the chart block MudBlazor renders beside them
/// unconditionally (it is emitted on every page even though nothing here renders a chart, and
/// the provider has no switch for it). The scrollbar block is not among them: MainLayout
/// turns it off with DefaultScrollbar "true" and app.css carries its rules verbatim.
///
/// The hashes are computed once at startup — Program.cs calls
/// <see cref="InitializeAsync"/> before the app serves anything, with the MudTheme the app
/// renders. Croft builds that theme once from the site's branding at startup; a MudTheme
/// change at runtime would render new text and need new hashes here — nothing in this class
/// would notice on its own.
/// </summary>
public sealed class MudThemeStyleCspContributor : IDocumentCspContributor
{
    private string[] _sources = [];

    /// <summary>
    /// Renders <paramref name="theme"/> in both modes and records one 'sha256-…' source per
    /// distinct style block's text. Called once at startup, before the first request.
    /// </summary>
    public async Task InitializeAsync(MudTheme theme)
    {
        List<string> sources = [];
        foreach (var isDarkMode in new[] { false, true })
        {
            foreach (var text in await MudThemeProviderCss.StyleTextsAsync(theme, isDarkMode))
            {
                var source = $"'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(text)))}'";
                if (!sources.Contains(source))
                {
                    sources.Add(source);
                }
            }
        }

        _sources = [.. sources];
    }

    public void Contribute(HttpContext context, CspPolicy policy, CspPolicy reportOnlyPolicy, CspDocumentKind kind)
    {
        // Only an interactive document renders MudThemeProvider (09i); the reading pages carry
        // no inline style block, so a hash there would allow a style that is not on the page.
        if (kind is not CspDocumentKind.Interactive)
        {
            return;
        }

        if (_sources is [])
        {
            throw new InvalidOperationException(
                "MudThemeStyleCspContributor.InitializeAsync must run at startup (Program.cs) before the app serves requests.");
        }

        policy.Add("style-src-elem", _sources);
    }

    public string? CacheKey(HttpContext context, CspDocumentKind kind) => "";
}
