namespace Croft.Web.Services;

/// <summary>
/// Every icon available for categories: name → SVG path content, plus name search for the
/// icon picker (spec §6.5, §8.4).
/// </summary>
public interface IIconCatalog
{
    /// <summary>All <c>Icons.Material.Filled</c> constant names, sorted.</summary>
    IReadOnlyList<string> Names { get; }

    /// <summary>SVG path data for the icon name; unknown or empty names give the Folder icon.</summary>
    string Resolve(string? name);

    /// <summary>Icon names matching the query: case-insensitive contains, prefix matches first.</summary>
    IReadOnlyList<string> Search(string query, int max = 200);
}
