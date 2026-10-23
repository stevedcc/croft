using System.Text;

namespace Croft.Web.Services;

/// <summary>The letter shown in the circle of a reply whose author has no avatar.</summary>
public static class AuthorInitial
{
    /// <summary>The first character of the display name (of the handle when the name is blank), upper-cased; "?" when there is none or the text starts with a lone surrogate (text from a remote server can be broken).</summary>
    public static string Of(string displayName, string handle)
    {
        var trimmed = (string.IsNullOrWhiteSpace(displayName) ? handle : displayName).TrimStart();
        return Rune.TryGetRuneAt(trimmed, 0, out var first) ? first.ToString().ToUpperInvariant() : "?";
    }
}
