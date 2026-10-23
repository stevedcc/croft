using System.Security.Cryptography;
using System.Text;

namespace Croft.Web.Theme;

/// <summary>
/// The theme.css the site serves — the generated palette CSS — and its cache-buster. The
/// stylesheet is served <c>immutable</c> for a year under <c>/branding/theme.css?v=</c>, so
/// <see cref="Version"/> is the hash of the exact served text: whatever changes a byte of it (the
/// site's palette, the banner URL) moves the URL, and no reader keeps a stale copy.
/// </summary>
public sealed class ThemeStylesheet
{
    /// <summary>The served stylesheet text.</summary>
    public string Css { get; private set; } = string.Empty;

    /// <summary>The first 16 hex digits of the SHA-256 of <see cref="Css"/> (UTF-8).</summary>
    public string Version { get; private set; } = string.Empty;

    /// <summary>Sets the served text (once, at startup).</summary>
    public void Initialize(string css)
    {
        Css = css;
        Version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Css)))[..16].ToLowerInvariant();
    }
}
