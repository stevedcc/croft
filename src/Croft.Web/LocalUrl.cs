namespace Croft.Web;

/// <summary>
/// Whether a request-supplied return target is a path on this site. The rules are ASP.NET Core's
/// <c>UrlHelperBase.CheckIsLocalUrl</c> (a leading "/" or "~/", then neither "/" nor "\" as the
/// next character, and no control character), plus no whitespace anywhere: browsers drop an ASCII
/// tab or newline anywhere in a URL (WHATWG), so "/&lt;TAB&gt;/evil.example" is "//evil.example".
/// The target becomes the sign-in's RedirectUri, which the framework redirects to verbatim.
/// </summary>
public static class LocalUrl
{
    public static bool IsLocal(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return false;

        // Allow ~/ prefix — treat it as root-relative
        if (url.StartsWith("~/", StringComparison.Ordinal))
            url = "/" + url[2..];

        // Must start with exactly one '/'
        if (!url.StartsWith('/'))
            return false;

        // Reject '//' (protocol-relative) and '/\' (backslash variant)
        if (url.Length > 1 && url[1] is '/' or '\\')
            return false;

        // Reject any control character or whitespace anywhere (the tab and newline a browser
        // strips, but also a space or vertical tab: nothing legitimate carries them unescaped).
        foreach (var character in url)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
                return false;
        }

        return true;
    }
}
