using System.Text;
using System.Text.RegularExpressions;

namespace Croft.Api.Social;

/// <summary>A span of a Bluesky post's text (offsets in UTF-8 bytes, as the protocol counts) that is a hashtag (<c>Tag</c>) or a link (<c>Uri</c>).</summary>
public sealed record BlueskyFacet(int ByteStart, int ByteEnd, string? Tag, string? Uri);

/// <summary>
/// The facets of an announcement (brief 12d): the link over the post's address and a tag facet for every hashtag. Bluesky does not find them by itself;
/// a post without facets shows plain text. Offsets count UTF-8 bytes, not characters. Hashtags are found the way the official
/// <c>@atproto/api</c> <c>detectFacets</c> finds them.
/// </summary>
public static partial class BlueskyFacets
{
    /// <summary>The longest tag Bluesky accepts (UTF-16 code units, as the official detection counts them); a longer one is not a facet.</summary>
    public const int MaxTagLength = 64;

    // A '#' (or the full-width one) at the start of the text or after white space, then a run of characters without white space that holds at least one character
    // that is not a digit or punctuation. Invisible joiners and format marks do not count as part of the run's "content".
    [GeneratedRegex(
        @"(^|\s)[#＃]((?!️)[^\s­⁠ ​‌‍⃢]*[^0-9\s\p{P}­⁠ ​‌‍⃢]+[^\s­⁠ ​‌‍⃢]*)",
        RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"\p{P}+$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingPunctuation();

    /// <summary>The hashtag facets of <paramref name="text"/>, in order. A tag loses its trailing punctuation; one that is empty or too long is skipped.</summary>
    public static IReadOnlyList<BlueskyFacet> Hashtags(string text)
    {
        List<BlueskyFacet> facets = [];
        foreach (Match match in TagPattern().Matches(text))
        {
            var run = match.Groups[2];
            var tag = TrailingPunctuation().Replace(run.Value.Trim(), string.Empty);
            if (tag.Length == 0 || tag.Length > MaxTagLength)
                continue;

            var start = run.Index - 1; // the '#' is one UTF-16 code unit
            facets.Add(new BlueskyFacet(ByteOffset(text, start), ByteOffset(text, run.Index + tag.Length), tag, null));
        }

        return facets;
    }

    /// <summary>The link facet over the last occurrence of <paramref name="postUrl"/> (where the composer puts it) and the hashtag facets, ordered by start.</summary>
    public static IReadOnlyList<BlueskyFacet> All(string text, string postUrl)
    {
        List<BlueskyFacet> facets = [.. Hashtags(text)];
        var at = text.LastIndexOf(postUrl, StringComparison.Ordinal);
        if (at >= 0)
            facets.Add(new BlueskyFacet(ByteOffset(text, at), ByteOffset(text, at + postUrl.Length), null, postUrl));

        return [.. facets.OrderBy(f => f.ByteStart)];
    }

    private static int ByteOffset(string text, int charIndex) => Encoding.UTF8.GetByteCount(text.AsSpan(0, charIndex));
}
