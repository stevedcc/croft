using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;

namespace Croft.Web.Tests;

/// <summary>
/// Renders MudThemeProvider on its own (Blazor's <see cref="HtmlRenderer"/> plus MudBlazor's
/// services and a JavaScript runtime that must never be used) so tests can derive their
/// expectations from a real render at test time. Deliberately independent of the app's own
/// render helper (Croft.Web.Security.Csp.MudThemeProviderCss): a bug in the app's text
/// extraction must fail the suite, not agree with itself here. The extracted style texts are
/// HTML-decoded — exactly the element text the browser's DOM holds and CSP hashes.
/// </summary>
internal static class MudThemeStyleRender
{
    /// <summary>
    /// The text of every inline style element the provider renders, in render order.
    /// <paramref name="defaultScrollbar"/> is the provider's parameter: true omits its
    /// scrollbar block.
    /// </summary>
    public static async Task<IReadOnlyList<string>> StyleTextsAsync(
        MudTheme theme, bool isDarkMode, bool defaultScrollbar)
    {
        var services = new ServiceCollection();
        services.AddMudServices();
        services.AddLogging();
        services.AddScoped<IJSRuntime, NoopJsRuntime>();
        await using var provider = services.BuildServiceProvider();
        var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = await renderer.RenderComponentAsync<MudThemeProvider>(ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    ["Theme"] = theme,
                    ["IsDarkMode"] = isDarkMode,
                    ["DefaultScrollbar"] = defaultScrollbar,
                }));
            return result.ToHtmlString();
        });
        return StyleTexts(html);
    }

    /// <summary>
    /// The scrollbar block's text, derived as the one block a render with DefaultScrollbar
    /// true omits — never transcribed by hand.
    /// </summary>
    public static async Task<string> ScrollbarStyleTextAsync(MudTheme theme)
    {
        var withBlock = await StyleTextsAsync(theme, isDarkMode: false, defaultScrollbar: false);
        var withoutBlock = await StyleTextsAsync(theme, isDarkMode: false, defaultScrollbar: true);
        return withBlock.Single(text => !withoutBlock.Contains(text));
    }

    /// <summary>The text of every inline style element in a rendered HTML document, decoded.</summary>
    public static IReadOnlyList<string> StyleTexts(string html) =>
    [
        .. Regex.Matches(html, "<style[^>]*>(?<text>.*?)</style>", RegexOptions.Singleline)
            .Select(match => WebUtility.HtmlDecode(match.Groups["text"].Value)),
    ];

    /// <summary>The CSP source that allows <paramref name="text"/> as an inline style element.</summary>
    public static string HashSource(string text) =>
        $"'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(text)))}'";

    private sealed class NoopJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new NotSupportedException("rendering MudThemeProvider must not need JavaScript");

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args) =>
            throw new NotSupportedException("rendering MudThemeProvider must not need JavaScript");
    }
}
