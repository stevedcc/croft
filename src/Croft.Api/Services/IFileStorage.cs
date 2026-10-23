namespace Croft.Api.Services;

public interface IFileStorage
{
    Task<string> SaveAsync(Stream data, string fileName, string contentType, string storagePath, CancellationToken ct = default);
    Stream OpenRead(string relativePath);
    void Delete(string relativePath);
}
