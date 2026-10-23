using System.Diagnostics.CodeAnalysis;

namespace Croft.Web.Services;

/// <summary>
/// Turns what a reader typed for "Your Mastodon server" into a DNS host name (brief 12b item 3), or refuses it.
/// The result goes into the host part of a link, so it must be nothing but a host: no scheme, user info, port,
/// path, query or fragment. Internationalised names are typed in their punycode form (<c>xn--...</c>).
/// </summary>
public static class MastodonServerName
{
    private const int MaxLength = 253;
    private const int MaxLabelLength = 63;

    /// <summary>
    /// Trims; lower-cases; drops a leading <c>https://</c> or <c>http://</c>, one trailing <c>/</c> and everything up to the
    /// last <c>@</c> (so <c>@me@mastodon.example</c> and <c>https://mastodon.example/</c> work); then requires a DNS host name:
    /// ASCII letters, digits, hyphens and dots, at least one dot, labels of 1-63 characters that neither start nor end with
    /// a hyphen, at most 253 characters in all, and a last label that is not all digits (so no IPv4 address).
    /// </summary>
    public static bool TryNormalise(string? input, [NotNullWhen(true)] out string? host)
    {
        host = null;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var value = input.Trim().ToLowerInvariant();
        foreach (var scheme in (ReadOnlySpan<string>)["https://", "http://"])
        {
            if (value.StartsWith(scheme, StringComparison.Ordinal))
            {
                value = value[scheme.Length..];
                break;
            }
        }

        if (value.EndsWith('/'))
        {
            value = value[..^1];
        }

        value = value[(value.LastIndexOf('@') + 1)..];

        if (!IsDnsHostName(value))
        {
            return false;
        }

        host = value;
        return true;
    }

    private static bool IsDnsHostName(string value)
    {
        if (value.Length is 0 or > MaxLength || !value.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        var labels = value.Split('.');
        foreach (var label in labels)
        {
            if (label.Length is 0 or > MaxLabelLength || label[0] == '-' || label[^1] == '-'
                || !label.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
            {
                return false;
            }
        }

        return !labels[^1].All(char.IsAsciiDigit);
    }
}
