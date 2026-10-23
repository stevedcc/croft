using Croft.Web.Data;

namespace Croft.Web.Services;

/// <summary>
/// The Web app's "keys import" entry ("dotnet Croft.Web.dll keys import &lt;directory&gt;"): copy
/// a file key ring's keys into the Web database exactly once, so switching the cluster's Data
/// Protection key storage signs nobody out. Idempotent by key id — a key the database already
/// holds is skipped and reported — so an interrupted cutover is fixed by re-running it. Without
/// ConnectionStrings:WebDb there is no key ring to import into, and the command fails loudly
/// instead of silently succeeding.
/// </summary>
public static class KeysImportCommand
{
    public static async Task<int> RunAsync(
        IServiceProvider services, string[] args, TextWriter output, TextWriter error, CancellationToken ct = default)
    {
        if (args is not ["keys", "import", var directory])
        {
            await error.WriteLineAsync("usage: dotnet Croft.Web.dll keys import <directory>");
            return 1;
        }

        await using var scope = services.CreateAsyncScope();
        if (scope.ServiceProvider.GetService<CroftWebDbContext>() is not { } db)
        {
            await error.WriteLineAsync(
                "keys import requires ConnectionStrings:WebDb: without a Web database there is no key ring to import into.");
            return 1;
        }

        if (!Directory.Exists(directory))
        {
            await error.WriteLineAsync($"keys import: no such directory: {directory}");
            return 1;
        }

        var result = await DataProtectionKeyImporter.ImportAsync(db, directory, ct);
        await output.WriteLineAsync(result.Imported.Count > 0
            ? $"Imported {result.Imported.Count} key(s): {string.Join(", ", result.Imported)}."
            : "Imported 0 key(s).");
        await output.WriteLineAsync(result.Skipped.Count > 0
            ? $"Skipped {result.Skipped.Count} key(s) already in the database: {string.Join(", ", result.Skipped)}."
            : "Skipped 0 key(s) already in the database.");
        return 0;
    }
}
