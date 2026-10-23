namespace Croft.Web.Components.Layout;

/// <summary>
/// A post's link-preview description: its <c>Excerpt</c> (the editor's "Search &amp; link-preview
/// description"), else its <c>Subtitle</c>, else the start of its summary text — plain text,
/// about 160 characters, cut at a word. One helper so every surface that describes a post
/// (the post page's head, the feed) applies the same fallback.
/// </summary>
public static class SeoDescription
{
    private const int MaxSummaryLength = 160;

    public static string FromPost(string? excerpt, string? subtitle, string? summary)
    {
        if (!string.IsNullOrWhiteSpace(excerpt))
            return excerpt;

        if (!string.IsNullOrWhiteSpace(subtitle))
            return subtitle;

        return TruncateAtWord(summary);
    }

    /// <summary>Cut at the last space at or before 160 characters (hard-cut when there is
    /// none), trim the tail and mark the cut with an ellipsis — the same rule the API's
    /// summary extractor applies at its own 300-character limit.</summary>
    private static string TruncateAtWord(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var trimmed = text.Trim();
        if (trimmed.Length <= MaxSummaryLength)
            return trimmed;

        var cut = trimmed.LastIndexOf(' ', MaxSummaryLength - 1);
        if (cut <= 0)
            cut = MaxSummaryLength;

        return trimmed[..cut].TrimEnd() + "…";
    }
}
