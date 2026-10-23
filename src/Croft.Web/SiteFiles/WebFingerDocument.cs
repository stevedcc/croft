using System.Text;
using System.Text.Json;

namespace Croft.Web;

/// <summary>
/// Brief 11a item 3: WebFinger (RFC 7033) JSON Resource Descriptor. When the site supplies a
/// <c>.well-known/webfinger</c> file, its bytes are parsed once at startup; the
/// <see cref="Subject"/> is the canonical account (<c>acct:user@host</c>) and
/// <see cref="Aliases"/> are the additional resource strings that should also resolve to this
/// document. The endpoint matches <c>?resource=r</c> against subject and aliases: the
/// <c>acct:</c> scheme and host compare case-insensitively, everything else exactly.
/// </summary>
/// <param name="Subject">The canonical resource string, e.g. <c>acct:steve@example.net</c>.</param>
/// <param name="Aliases">Additional resource strings that resolve to this document.</param>
public sealed record WebFingerDocument(string Subject, IReadOnlyList<string> Aliases)
{
    /// <summary>The one file that may carry a WebFinger document, relative to the site-files
    /// folder. RFC 7033 fixes the path.</summary>
    public const string RelativePath = ".well-known/webfinger";

    /// <summary>The URL RFC 7033 fixes for it, whatever case the site's file name used.</summary>
    public const string UrlPath = "/" + RelativePath;

    /// <summary>Whether the request's <c>resource</c> parameter <paramref name="resource"/>
    /// matches <see cref="Subject"/> or one of <see cref="Aliases"/>: the <c>acct:</c> scheme
    /// and host compare case-insensitively (<c>ACCT:steve@EXAMPLE.NET</c> ≡
    /// <c>acct:steve@example.net</c>), the local part and any non-<c>acct:</c> resource
    /// exactly (brief 11a item 3).</summary>
    public bool Matches(string resource)
    {
        if (string.Equals(resource, Subject, StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var alias in Aliases)
        {
            if (string.Equals(resource, alias, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return AcctHostEquals(resource, Subject)
            || Aliases.Any(alias => AcctHostEquals(resource, alias));
    }

    /// <summary>True when both <paramref name="a"/> and <paramref name="b"/> start with
    /// <c>acct:</c> (any scheme case) and the local parts are exact-equal and the host parts
    /// are case-insensitive-equal.</summary>
    private static bool AcctHostEquals(string a, string b)
    {
        if (!a.StartsWith("acct:", StringComparison.OrdinalIgnoreCase)
            || !b.StartsWith("acct:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var aLocalHost = SplitAcct(a);
        var bLocalHost = SplitAcct(b);
        if (aLocalHost is null || bLocalHost is null)
        {
            return false;
        }

        return string.Equals(aLocalHost.Value.Local, bLocalHost.Value.Local, StringComparison.Ordinal)
            && string.Equals(aLocalHost.Value.Host, bLocalHost.Value.Host, StringComparison.OrdinalIgnoreCase);
    }

    private static (string Local, string Host)? SplitAcct(string value)
    {
        // The 'acct:' scheme's separator is '@': the local part is between ':' and '@', the
        // host is after '@'. Split the value on those two characters.
        var colon = value.IndexOf(':');
        var at = value.IndexOf('@', colon + 1);
        if (colon < 0 || at < 0)
        {
            return null;
        }

        var local = value[(colon + 1)..at];
        var host = value[(at + 1)..];
        if (local.Length == 0 || host.Length == 0)
        {
            return null;
        }

        return (local, host);
    }

    /// <summary>Parse the file's bytes into a <see cref="WebFingerDocument"/>. Throws
    /// <see cref="SiteFilesException"/> when the JSON is invalid or the required
    /// <c>subject</c> field is missing or not a string; the brief says "otherwise startup
    /// fails, naming the file". RFC 7033's field names are exact (<c>subject</c>,
    /// <c>aliases</c>).</summary>
    public static WebFingerDocument Parse(string file, byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(text);
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new SiteFilesException($"webfinger file '{file}' is not valid JSON: {ex.Message}");
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new SiteFilesException(
                $"webfinger file '{file}' must be a JSON object (RFC 7033), not {root.ValueKind}");
        }

        if (!root.TryGetProperty("subject", out var subjectValue))
        {
            throw new SiteFilesException(
                $"webfinger file '{file}' is missing the required 'subject' string field (RFC 7033)");
        }

        if (subjectValue.ValueKind != JsonValueKind.String)
        {
            throw new SiteFilesException(
                $"webfinger file '{file}': the 'subject' field must be a string");
        }

        var subject = subjectValue.GetString()!;
        var aliases = new List<string>();
        if (root.TryGetProperty("aliases", out var aliasesValue))
        {
            if (aliasesValue.ValueKind != JsonValueKind.Array)
            {
                throw new SiteFilesException(
                    $"webfinger file '{file}': the 'aliases' field must be an array of strings");
            }

            foreach (var alias in aliasesValue.EnumerateArray())
            {
                if (alias.ValueKind != JsonValueKind.String)
                {
                    throw new SiteFilesException(
                        $"webfinger file '{file}': every 'aliases' entry must be a string");
                }

                aliases.Add(alias.GetString()!);
            }
        }

        return new WebFingerDocument(subject, aliases);
    }
}
