using Microsoft.AspNetCore.WebUtilities;

namespace Croft.Web;

/// <summary>Reads the list pages' ?page=N number (spec §5.2) from a raw query string.</summary>
public static class PageQuery
{
    /// <summary>The page number from ?page=N; a missing or invalid value is page 1.</summary>
    public static int ReadPageNumber(string? query)
    {
        var values = QueryHelpers.ParseQuery(query);
        return values.TryGetValue("page", out var value) && int.TryParse(value, out var page) && page > 0
            ? page
            : 1;
    }
}
