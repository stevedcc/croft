namespace Croft.Web.Security.Csp;

/// <summary>
/// The theme bootstrap script App.razor renders at the top of the document head: the one
/// source both the rendered markup and the CSP hash that allows it come from. The script
/// must run before first paint (it applies the stored theme and sidebar state to the html
/// element), so it stays inline; keeping the text here is what keeps the rendered script
/// and the hashed text byte-identical.
/// </summary>
public static class ThemeBootstrapScript
{
    /// <summary>The script exactly as it must appear inside the rendered script element.</summary>
    public const string Text = """
        (function () {
          try {
            var t = localStorage.getItem('blog-theme');
            if (t === 'light' || t === 'dark') document.documentElement.setAttribute('data-theme', t);
            if (localStorage.getItem('blog-sidebar') === 'collapsed') document.documentElement.classList.add('sidebar-collapsed');
          } catch (e) { /* storage blocked: defaults apply */ }
        })();
        """;
}
