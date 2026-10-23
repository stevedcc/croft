using System.Reflection;
using Croft.Api.Data;
using Croft.Shared;

namespace Croft.Api.Services.ContentExport;

/// <summary>A failure the owner can fix (existing content, an unknown format, a missing file): logged with its message, exit code 1.</summary>
public sealed class ContentTransferException(string message) : Exception(message);

/// <summary>
/// The API image's "export" and "import" console commands, run like "migrate": they never throw
/// out of the process (an unhandled exception at PID 1 was seen to leave the container running),
/// they return the exit code. A database that is away is retried until
/// <c>Database:MigrateTimeout</c> (the policy of <see cref="MigrationRetry"/>), then exit code 1.
/// </summary>
public static partial class ContentCommands
{
    public static Task<int> ExportAsync(
        IServiceProvider services, IConfiguration configuration, ILogger logger, TimeProvider clock, string outputDirectory) =>
        RunAsync(configuration, logger, clock, async () =>
        {
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
            var manifest = await ContentExporter.ExportAsync(db, outputDirectory, clock.GetUtcNow(), CroftVersion());
            LogExported(logger, outputDirectory, manifest.Counts.Posts, manifest.Counts.Pages);
        });

    public static Task<int> ImportAsync(
        IServiceProvider services, IConfiguration configuration, ILogger logger, TimeProvider clock, string sourceDirectory) =>
        RunAsync(configuration, logger, clock, async () =>
        {
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
            var mediaRoot = configuration.GetValue("FileStorage:BasePath", "uploads")!;
            var manifest = await ContentImporter.ImportAsync(db, sourceDirectory, mediaRoot, logger);
            LogImported(logger, sourceDirectory, manifest.Counts.Posts, manifest.Counts.Pages);
        });

    private static async Task<int> RunAsync(
        IConfiguration configuration, ILogger logger, TimeProvider clock, Func<Task> work)
    {
        var refused = false;
        var exitCode = await MigrationRetry.RunEntryAsync(configuration, logger, clock, async () =>
        {
            try
            {
                await work();
            }
            catch (ContentTransferException exception)
            {
                LogRefused(logger, exception.Message);
                refused = true;
            }
        });
        return refused ? 1 : exitCode;
    }

    internal static string CroftVersion() =>
        typeof(ContentCommands).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(ContentCommands).Assembly.GetName().Version?.ToString() ?? "unknown";

    [LoggerMessage(Level = LogLevel.Information, Message = "Exported {Posts} posts and {Pages} pages to {Directory}.")]
    private static partial void LogExported(ILogger logger, string directory, int posts, int pages);

    [LoggerMessage(Level = LogLevel.Information, Message = "Imported {Posts} posts and {Pages} pages from {Directory}.")]
    private static partial void LogImported(ILogger logger, string directory, int posts, int pages);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Message}")]
    private static partial void LogRefused(ILogger logger, string message);
}
