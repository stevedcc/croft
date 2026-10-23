using System.Net;
using Microsoft.AspNetCore.Antiforgery;

namespace Croft.Web.Authentication;

/// <summary>
/// The sign-out interstitial served at <see cref="Path"/>. Sign-out must clear every local draft
/// backup first (a shared computer keeps no drafts after sign-out), and only same-origin
/// JavaScript can reach localStorage — so GET /logout redirects here, and the page's external
/// script clears the keys and submits the form. The form POSTs to /logout/complete (the cookie
/// and IdP sign-out) with an antiforgery token, so a page on another site cannot sign a staff
/// member out (brief 13b); its visible submit button is the no-script fallback. The page is
/// rendered per request because it carries the token. CSP-clean by construction: external
/// script only, no inline script and no style attribute.
/// </summary>
public static class SignOutInterstitial
{
    public const string Path = "/logout/interstitial";

    /// <summary>The sign-out's continuation endpoint (POST only), where <see cref="TokenSessionEvents.LogoutAsync"/> runs.</summary>
    public const string CompletePath = "/logout/complete";

    /// <summary>The page with its antiforgery form field; <paramref name="tokens"/> comes from <c>IAntiforgery.GetAndStoreTokens</c>.</summary>
    public static string Render(AntiforgeryTokenSet tokens)
    {
        var field = WebUtility.HtmlEncode(tokens.FormFieldName);
        var value = WebUtility.HtmlEncode(tokens.RequestToken);
        return $"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="robots" content="noindex">
            <title>Signing out</title>
            </head>
            <body>
            <p>Signing out…</p>
            <form method="post" action="{CompletePath}" id="logout-form">
            <input type="hidden" name="{field}" value="{value}">
            <button type="submit">Continue signing out</button>
            </form>
            <script src="/js/logout.js"></script>
            </body>
            </html>
            """;
    }

    // WORKAROUND(W-096, Chromium): Chromium checks form-action on the redirect hop of a form POST, so a 302 to the IdP is blocked under form-action 'self'; the POST answers a 200 page that continues by meta refresh
    /// <summary>
    /// The page the sign-out POST answers instead of a redirect. Chromium applies
    /// <c>form-action 'self'</c> to the redirect target of a form submission, so a 302 from
    /// <see cref="CompletePath"/> to the IdP's end-session endpoint is blocked (and widening the
    /// policy is not on the table). A 200 page that continues by <c>meta refresh</c> is not a form
    /// redirect, needs no script and no inline style, and its visible link is the fallback for a
    /// browser that ignores the refresh. <paramref name="url"/> is HTML-encoded here.
    /// </summary>
    public static string RenderContinue(string url)
    {
        var encoded = WebUtility.HtmlEncode(url);
        return $"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="robots" content="noindex">
            <meta http-equiv="refresh" content="0;url={encoded}">
            <title>Signed out</title>
            </head>
            <body>
            <p>Signing out…</p>
            <p><a href="{encoded}">Continue signing out</a></p>
            </body>
            </html>
            """;
    }
    // END WORKAROUND(W-096)
}
