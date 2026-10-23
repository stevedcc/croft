namespace Croft.Api.Services;

public class LocalFileStorage(string basePath) : IFileStorage
{
    public async Task<string> SaveAsync(Stream data, string fileName, string contentType, string storagePath, CancellationToken ct = default)
    {
        var directory = Path.Combine(basePath, storagePath);
        Directory.CreateDirectory(directory);

        var safeName = SanitizeFileName(fileName);
        var uniqueName = $"{Guid.NewGuid():N}{Path.GetExtension(safeName)}";
        var fullPath = Path.Combine(directory, uniqueName);

        await using var fileStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        await data.CopyToAsync(fileStream, ct);

        var relativePath = Path.Combine(storagePath, uniqueName).Replace('\\', '/');
        return relativePath;
    }

    public Stream OpenRead(string relativePath)
    {
        var fullPath = GetFullPath(relativePath);
        return File.OpenRead(fullPath);
    }

    public void Delete(string relativePath)
    {
        var fullPath = GetFullPath(relativePath);
        if (File.Exists(fullPath))
            File.Delete(fullPath);
    }

    /// <summary>The base folder as a full path ending in a separator: a path is inside it only if it
    /// starts with this, so a sibling such as <c>uploads-old</c> beside <c>uploads</c> is outside.</summary>
    private readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(basePath)) + Path.DirectorySeparatorChar;

    private string GetFullPath(string relativePath)
    {
        var combined = Path.Combine(basePath, relativePath);
        var fullPath = Path.GetFullPath(combined);

        // Ordinal: the file system under the container is case-sensitive, and a case-folding match
        // would let /DATA/UPLOADS/.. through this check only to fail on the file system.
        if (!fullPath.StartsWith(_root, StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid file path.");

        return fullPath;
    }

    private static string SanitizeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }
}
