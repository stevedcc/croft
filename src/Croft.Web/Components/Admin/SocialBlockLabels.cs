using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Croft.Shared.Contracts.Social;

namespace Croft.Web.Components.Admin;

/// <summary>
/// The labels a block carries, so the "Hidden replies" list makes sense without the reply (brief 12c):
/// a reply block reads <c>@handle: first 80 characters of the text</c>, an account block
/// <c>Display name (@handle)</c>.
/// </summary>
public static partial class SocialBlockLabels
{
    /// <summary>How much of the reply's text the label keeps.</summary>
    public const int TextLength = 80;

    public static string ForReply(DiscussionReplyDto reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        return $"@{reply.AuthorHandle}: {Excerpt(reply.ContentHtml)}";
    }

    public static string ForAccount(DiscussionReplyDto reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        return $"{reply.AuthorDisplayName} (@{reply.AuthorHandle})";
    }

    /// <summary>The reply's text: block ends become spaces, the other tags go, entities are decoded, whitespace is
    /// collapsed; cut at <see cref="TextLength"/> characters (never inside a surrogate pair) with "…" when cut.</summary>
    private static string Excerpt(string html)
    {
        var text = Whitespace().Replace(WebUtility.HtmlDecode(Tags().Replace(BlockEnds().Replace(html, " "), string.Empty)), " ").Trim();
        var kept = new StringBuilder();
        var count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (count == TextLength)
            {
                return kept + "…";
            }

            kept.Append(rune);
            count++;
        }

        return text;
    }

    [GeneratedRegex(@"</p\s*>|<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEnds();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
