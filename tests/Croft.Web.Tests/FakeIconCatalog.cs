using Croft.Web.Services;
using MudBlazor;

namespace Croft.Web.Tests;

/// <summary>
/// Tiny deterministic icon catalogue for component tests (Task R2.4's bUnit setup): three real
/// MudBlazor path values, the Folder fallback for anything unknown, simple contains search.
/// </summary>
internal sealed class FakeIconCatalog : IIconCatalog
{
    private static readonly Dictionary<string, string> Paths = new()
    {
        ["Folder"] = Icons.Material.Filled.Folder,
        ["Landscape"] = Icons.Material.Filled.Landscape,
        ["Terrain"] = Icons.Material.Filled.Terrain,
    };

    public IReadOnlyList<string> Names { get; } =
        [.. Paths.Keys.OrderBy(n => n, StringComparer.Ordinal)];

    public string Resolve(string? name) =>
        name is not null && Paths.TryGetValue(name, out var path) ? path : Icons.Material.Filled.Folder;

    public IReadOnlyList<string> Search(string query, int max = 200) =>
        [.. Names.Where(n => n.Contains(query?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase)).Take(max)];
}
