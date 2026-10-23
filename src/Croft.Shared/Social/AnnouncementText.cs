using System.Globalization;
using System.Text.RegularExpressions;
using Croft.Shared.Contracts.Social;

namespace Croft.Shared.Social;

/// <summary>
/// The announcement of a post on Mastodon and Bluesky (brief 12d): the limits, how each network counts, the default text and its fitting.
/// Used by the API (validation, sending) and the Web (the editor's preview and counts), so both count the same way.
/// An announcement is <c>{body}\n\n{postUrl}</c> (<see cref="Compose"/>); the limits apply to the whole of it.
/// </summary>
public static partial class AnnouncementText
{
    public const int MastodonLimit = 500;
    public const int BlueskyLimit = 300;

    /// <summary>Mastodon counts every URL as this many characters, whatever its length.</summary>
    public const int MastodonUrlLength = 23;

    public const string Ellipsis = "…";

    public static int Limit(SocialPlatform platform) => platform switch
    {
        SocialPlatform.Mastodon => MastodonLimit,
        SocialPlatform.Bluesky => BlueskyLimit,
        _ => throw new ArgumentOutOfRangeException(nameof(platform)),
    };

    /// <summary>Grapheme clusters, with every URL counted as <see cref="MastodonUrlLength"/> (the way Mastodon counts).</summary>
    public static int MastodonLength(string text)
    {
        var length = 0;
        var last = 0;
        foreach (var url in UrlPattern().EnumerateMatches(text))
        {
            length += Graphemes(text.AsSpan(last, url.Index - last)) + MastodonUrlLength;
            last = url.Index + url.Length;
        }

        return length + Graphemes(text.AsSpan(last));
    }

    /// <summary>Grapheme clusters, a URL counted as written (the way Bluesky counts).</summary>
    public static int BlueskyLength(string text) => Graphemes(text);

    public static int Length(SocialPlatform platform, string text) => platform switch
    {
        SocialPlatform.Mastodon => MastodonLength(text),
        SocialPlatform.Bluesky => BlueskyLength(text),
        _ => throw new ArgumentOutOfRangeException(nameof(platform)),
    };

    /// <summary>The address of a post: the site's address without a trailing slash, <c>/post/</c> and the slug. Without a site address, the path alone.</summary>
    public static string PostUrl(string? baseUrl, string slug) => $"{baseUrl?.Trim().TrimEnd('/')}/post/{slug}";

    public static string Compose(string body, string postUrl) => $"{body}\n\n{postUrl}";

    /// <summary>The most hashtags the default announcement appends.</summary>
    public const int MaxHashtags = 3;

    /// <summary>
    /// The hashtags the default text of a post gets on <paramref name="platform"/> as <c>#Name</c> strings: the category's first, then each tag's in the order given
    /// (the order the post lists its tags in). Entries for the other network are skipped, duplicates that differ only in case are dropped keeping the first,
    /// and at most <see cref="MaxHashtags"/> are taken.
    /// </summary>
    public static IReadOnlyList<string> SelectHashtags(
        SocialPlatform platform, IReadOnlyList<HashtagDto> category, IEnumerable<IReadOnlyList<HashtagDto>> tags) =>
    [
        .. category.Concat(tags.SelectMany(tag => tag))
            .Where(hashtag => AppliesTo(hashtag.Networks, platform))
            .Select(hashtag => hashtag.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxHashtags)
            .Select(name => "#" + name)
    ];

    private static bool AppliesTo(HashtagNetworks networks, SocialPlatform platform) => networks switch
    {
        HashtagNetworks.Both => true,
        HashtagNetworks.MastodonOnly => platform == SocialPlatform.Mastodon,
        HashtagNetworks.BlueskyOnly => platform == SocialPlatform.Bluesky,
        _ => throw new ArgumentOutOfRangeException(nameof(networks)),
    };

    /// <summary>The text used when the post has none of its own: the title, then a blank line and the subtitle when there is one.</summary>
    public static string Default(string title, string? subtitle) => Default(title, subtitle, []);

    /// <summary><see cref="Default(string, string?)"/>, then a blank line and the hashtags joined by single spaces when there are any.</summary>
    public static string Default(string title, string? subtitle, IReadOnlyList<string> hashtags)
    {
        var text = string.IsNullOrWhiteSpace(subtitle) ? title : $"{title}\n\n{subtitle}";
        return hashtags.Count == 0 ? text : $"{text}\n\n{string.Join(' ', hashtags)}";
    }

    /// <summary><see cref="FitDefault(SocialPlatform, string, string?, IReadOnlyList{string}, string)"/> without hashtags.</summary>
    public static string FitDefault(SocialPlatform platform, string title, string? subtitle, string postUrl) =>
        FitDefault(platform, title, subtitle, [], postUrl);

    /// <summary>
    /// <see cref="Default(string, string?, IReadOnlyList{string})"/>, shortened when the announcement would pass the network's limit, in this order until it fits:
    /// the whole text; the subtitle cut (grapheme by grapheme, ending in "…") with the hashtags kept; no subtitle and all hashtags; no subtitle and the hashtags
    /// dropped from the end one at a time; no hashtags and the title cut the same way as the subtitle. Never fails and never cuts inside a grapheme cluster.
    /// </summary>
    public static string FitDefault(SocialPlatform platform, string title, string? subtitle, IReadOnlyList<string> hashtags, string postUrl)
    {
        var full = Default(title, subtitle, hashtags);
        if (Fits(platform, full, postUrl))
            return full;

        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            var cut = LongestPrefix(subtitle, text => Fits(platform, Default(title, text + Ellipsis, hashtags), postUrl));
            if (cut.Length > 0)
                return Default(title, cut + Ellipsis, hashtags);
        }

        for (var kept = hashtags.Count; kept > 0; kept--)
        {
            var withoutSubtitle = Default(title, null, [.. hashtags.Take(kept)]);
            if (Fits(platform, withoutSubtitle, postUrl))
                return withoutSubtitle;
        }

        if (Fits(platform, title, postUrl))
            return title;

        return LongestPrefix(title, text => Fits(platform, text + Ellipsis, postUrl)) + Ellipsis;
    }

    private static bool Fits(SocialPlatform platform, string body, string postUrl) =>
        Length(platform, Compose(body, postUrl)) <= Limit(platform);

    /// <summary>The longest prefix of whole grapheme clusters, trailing white space trimmed, for which <paramref name="fits"/> holds (empty when none does).</summary>
    private static string LongestPrefix(string text, Func<string, bool> fits)
    {
        var ends = new List<int>();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
            ends.Add(enumerator.ElementIndex + enumerator.GetTextElement().Length);

        // A longer prefix is never shorter in either count, so "fits" is monotonic: search for the last prefix that does.
        int low = 0, high = ends.Count;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (fits(text[..ends[middle - 1]].TrimEnd()))
                low = middle;
            else
                high = middle - 1;
        }

        return low == 0 ? string.Empty : text[..ends[low - 1]].TrimEnd();
    }

    private static int Graphemes(ReadOnlySpan<char> text)
    {
        var count = 0;
        for (var rest = text; !rest.IsEmpty; count++)
            rest = rest[StringInfo.GetNextTextElementLength(rest)..];
        return count;
    }

    // Any absolute http(s) URL up to the next white space: what Mastodon replaces by its fixed length.
    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();
}
