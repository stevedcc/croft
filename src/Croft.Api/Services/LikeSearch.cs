namespace Croft.Api.Services;

/// <summary>LIKE/ILIKE pattern building for the list endpoints' <c>q</c> filters.</summary>
internal static class LikeSearch
{
    /// <summary>
    /// A pattern matching <paramref name="text"/> as a case-insensitive substring, with the
    /// user's own wildcards escaped so a "%" in the filter searches for a literal percent sign.
    /// </summary>
    public static string ContainsPattern(string text) =>
        $"%{text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
}
