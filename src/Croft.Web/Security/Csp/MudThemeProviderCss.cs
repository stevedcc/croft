// WORKAROUND-FILE(W-019, MudBlazor): MudThemeProvider builds its inline style text itself, so it is rendered at start-up to hash each block for the CSP
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;

namespace Croft.Web.Security.Csp;

/// <summary>
/// The inline &lt;style&gt; blocks MudThemeProvider renders, taken by rendering the component
/// with Blazor's HtmlRenderer — the only way to get each block's text byte-for-byte as the
/// browser's DOM will hold it (what a CSP hash is computed over), because MudBlazor generates
/// the text inside the component. The render runs with MudBlazor's own services and a
/// JavaScript runtime that must never fire: the blocks are built from the theme parameters
/// alone, and static rendering never reaches OnAfterRender. MainLayout renders the provider
/// with DefaultScrollbar "true" (its inline scrollbar block is replaced by the copy in
/// app.css), so that is what this renders: the theme variables of the given mode and the
/// chart block MudBlazor always emits beside them.
/// </summary>
public static class MudThemeProviderCss
{
    public static async Task<IReadOnlyList<string>> StyleTextsAsync(MudTheme theme, bool isDarkMode)
    {
        var services = new ServiceCollection();
        services.AddMudServices();
        services.AddLogging();
        services.AddScoped<IJSRuntime, NoopJsRuntime>();
        await using var provider = services.BuildServiceProvider();

        var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = await renderer.RenderComponentAsync<MudThemeProvider>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["Theme"] = theme,
                    ["IsDarkMode"] = isDarkMode,
                    ["DefaultScrollbar"] = true,
                }));
            return result.ToHtmlString();
        });

        return
        [
            .. Regex.Matches(html, "<style[^>]*>(?<text>.*?)</style>", RegexOptions.Singleline)
                .Select(match => WebUtility.HtmlDecode(match.Groups["text"].Value)),
        ];
    }

    /// <summary>MudThemeProvider injects a JS runtime it never uses while rendering.</summary>
    private sealed class NoopJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new NotSupportedException("rendering MudThemeProvider must not need JavaScript");

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args) =>
            throw new NotSupportedException("rendering MudThemeProvider must not need JavaScript");
    }
}
