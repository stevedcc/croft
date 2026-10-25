using System.Xml.Linq;
using Croft.Web.Data;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Croft.Web.Services;

/// <summary>
/// Copies the key XMLs of a file key ring into the Web database's key ring, once: a key the
/// database already holds (by the key id in its XML) is skipped, so re-running after an
/// interrupted cutover imports only what is missing. The keys themselves do not change — only
/// where they live — so every cookie they protected stays readable and nobody is signed out by
/// the switch.
/// </summary>
public static class DataProtectionKeyImporter
{
    public static async Task<DataProtectionKeyImportResult> ImportAsync(
        CroftWebDbContext db, string directory, CancellationToken ct = default)
    {
        // What the database's key ring already holds, by key id: the framework's repository reads
        // these same rows, so a key present here is one every instance can already use.
        HashSet<string> knownIds = [with(StringComparer.Ordinal)];
        foreach (var key in await db.DataProtectionKeys.AsNoTracking().ToListAsync(ct))
        {
            if (KeyId(key.Xml) is { } id)
            {
                knownIds.Add(id);
            }
        }

        List<string> imported = [];
        List<string> skipped = [];
        List<DataProtectionKey> rows = [];
        foreach (var file in Directory.EnumerateFiles(directory, "*.xml").Order(StringComparer.Ordinal))
        {
            var xml = await File.ReadAllTextAsync(file, ct);
            var id = RequireKeyId(xml, file);
            var name = Path.GetFileNameWithoutExtension(file);
            if (!knownIds.Add(id))
            {
                skipped.Add(name);
                continue;
            }

            imported.Add(name);
            rows.Add(new DataProtectionKey { FriendlyName = name, Xml = xml });
        }

        if (rows.Count > 0)
        {
            db.DataProtectionKeys.AddRange(rows);
            await db.SaveChangesAsync(ct);
        }

        return new DataProtectionKeyImportResult(imported, skipped);
    }

    private static string? KeyId(string? xml) =>
        string.IsNullOrEmpty(xml) ? null : XElement.Parse(xml).Attribute("id")?.Value;

    private static string RequireKeyId(string xml, string file)
    {
        var element = XElement.Parse(xml);
        var id = element.Name.LocalName == "key" ? element.Attribute("id")?.Value : null;
        if (string.IsNullOrEmpty(id))
        {
            // Fail loudly: silently skipping a file that turned out to be a key would leave the
            // database without it and sign everybody that key protected out at the cutover.
            throw new InvalidDataException($"'{file}' is not a Data Protection key XML (expected a <key id=\"...\"> root).");
        }

        return id;
    }
}
