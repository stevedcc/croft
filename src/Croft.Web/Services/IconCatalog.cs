using System.Reflection;
using MudBlazor;

namespace Croft.Web.Services;

/// <summary>
/// Singleton catalogue of every <see cref="Icons.Material.Filled"/> icon: name → SVG path content,
/// plus name search for the icon picker (spec §6.5, §8.4). The reflection over the constants runs
/// once, here in the constructor.
/// </summary>
public sealed class IconCatalog : IIconCatalog
{
    // Spec §6.5 says the picker matches "mount" on name (→ Terrain), but no Material icon name
    // contains "mount", so a tiny curated synonym map supplies those matches.
    private static readonly IReadOnlyDictionary<string, string[]> Synonyms =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["mount"] = ["Terrain", "Landscape"],
        };

    private readonly IReadOnlyDictionary<string, string> _paths;
    private readonly IReadOnlyList<string> _names;

    // WORKAROUND(W-036, MudBlazor): relies on Icons.Material.Filled being public static string fields holding SVG path markup
    public IconCatalog()
    {
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in typeof(Icons.Material.Filled)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string)))
        {
            if (field.GetValue(null) is string value)
            {
                paths[field.Name] = value;
            }
        }

        _paths = paths;
        _names = [.. paths.Keys.OrderBy(n => n, StringComparer.Ordinal)];
    }
    // END WORKAROUND(W-036)

    public IReadOnlyList<string> Names => _names;

    public string Resolve(string? name) =>
        name is not null && _paths.TryGetValue(name, out var path) ? path : Icons.Material.Filled.Folder;

    public IReadOnlyList<string> Search(string query, int max = 200)
    {
        if (max <= 0)
        {
            return [];
        }

        var term = query?.Trim() ?? string.Empty;
        if (term.Length == 0)
        {
            return [.. _names.Take(max)];
        }

        var matches = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in _names)
        {
            if (name.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(name);
            }
        }

        if (Synonyms.TryGetValue(term, out var synonyms))
        {
            foreach (var name in synonyms)
            {
                if (_paths.ContainsKey(name))
                {
                    matches.Add(name);
                }
            }
        }

        return [.. matches
            .OrderByDescending(n => n.StartsWith(term, StringComparison.OrdinalIgnoreCase))
            .ThenBy(n => n, StringComparer.Ordinal)
            .Take(max)];
    }
}
