using System.Text.RegularExpressions;
using Croft.Shared.Contracts.Social;

namespace Croft.Shared.Social;

/// <summary>
/// The rules for the hashtags of a category or tag, shared by the API (validation) and the Web form (the message under the row), so both agree.
/// A name is stored without the <c>#</c>; the rules accept only names the Bluesky facet detection turns into exactly that tag.
/// </summary>
public static partial class Hashtags
{
    public const int MaxPerItem = 10;
    public const int MaxNameLength = 64;

    /// <summary>The name as stored: a leading <c>#</c> or <c>＃</c> removed, surrounding white space trimmed.</summary>
    public static string Normalize(string? input)
    {
        var text = (input ?? string.Empty).Trim();
        return text.Length > 0 && text[0] is '#' or '＃' ? text[1..].Trim() : text;
    }

    /// <summary>Why a normalized name is not a hashtag, or null when it is one.</summary>
    public static string? NameProblem(string name)
    {
        if (name.Length == 0)
            return "a hashtag cannot be empty.";

        if (name.Length > MaxNameLength)
            return $"'#{name[..16]}…' has more than {MaxNameLength} characters.";

        if (!AllowedCharacters().IsMatch(name))
            return "'#" + name + "' may contain only letters, digits and underscores.";

        if (!name.Any(char.IsLetter))
            return "'#" + name + "' needs at least one letter.";

        // Bluesky drops punctuation at the end of a tag, an underscore included, so a trailing one would be posted as a different tag.
        if (name[^1] == '_')
            return "'#" + name + "' cannot end with an underscore.";

        return null;
    }

    /// <summary>
    /// One entry per hashtag of <paramref name="hashtags"/>: null when the row is fine, else the message to show under it. A row is flagged for its name,
    /// for repeating an earlier row's name in other letter case, and for being beyond the <see cref="MaxPerItem"/>th.
    /// </summary>
    public static IReadOnlyList<string?> RowProblems(IReadOnlyList<HashtagDto> hashtags)
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var problems = new string?[hashtags.Count];
        for (var i = 0; i < hashtags.Count; i++)
        {
            var name = Normalize(hashtags[i].Name);
            problems[i] = NameProblem(name);
            if (problems[i] is null && seen.TryGetValue(name, out var first))
                problems[i] = $"'#{name}' repeats '#{first}'; hashtags that differ only in letter case are the same.";
            else if (problems[i] is null)
                seen[name] = name;

            if (problems[i] is null && i >= MaxPerItem)
                problems[i] = $"'#{name}' is too many: at most {MaxPerItem} hashtags.";
        }

        return problems;
    }

    /// <summary>The list as it is stored (names normalized), or the first problem as a message naming <paramref name="field"/> and the hashtag.</summary>
    public static bool TryNormalize(IReadOnlyList<HashtagDto> hashtags, string field, out List<HashtagDto> normalized, out string? error)
    {
        var problems = RowProblems(hashtags);
        for (var i = 0; i < problems.Count; i++)
        {
            if (problems[i] is { } problem)
            {
                normalized = [];
                error = $"{field}: {problem}";
                return false;
            }
        }

        normalized = [.. hashtags.Select(h => h with { Name = Normalize(h.Name) })];
        error = null;
        return true;
    }

    [GeneratedRegex(@"^[\p{L}\p{Nd}_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex AllowedCharacters();
}
