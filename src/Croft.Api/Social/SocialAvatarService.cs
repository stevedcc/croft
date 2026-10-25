using System.Security.Cryptography;
using Croft.Api.Data;
using Croft.Api.Models;
using Croft.Api.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SkiaSharp;

namespace Croft.Api.Social;

/// <summary>
/// Avatars of replies (brief 12a): each distinct source URL is downloaded once (up to 1 MB), decoded (PNG, JPEG, WebP or GIF, the first frame,
/// at most 4096 x 4096, checked from the header before decoding), centre-cropped to a square and re-encoded to a 48 x 48 WebP, which strips
/// every piece of metadata and defuses a malicious image, then stored through file storage under the SHA-256 of the WebP. A bad avatar never fails a
/// thread: it is left out and that reply shows the fallback.
/// </summary>
public sealed partial class SocialAvatarService(IHttpClientFactory clients, IFileStorage storage, TimeProvider time, ILogger<SocialAvatarService> logger) : ISocialAvatars
{
    public const int Size = 48;

    public const int MaxSide = 4096;

    public const string Folder = "social-avatars";

    /// <summary>
    /// An avatar younger than this is never swept even when no reply references it yet: another replica may have just downloaded it and not yet
    /// stored the replies that use it (a refresh resolves avatars, then writes the thread).
    /// </summary>
    public static readonly TimeSpan OrphanGrace = TimeSpan.FromHours(1);

    private static readonly SKEncodedImageFormat[] AllowedFormats =
        [SKEncodedImageFormat.Png, SKEncodedImageFormat.Jpeg, SKEncodedImageFormat.Webp, SKEncodedImageFormat.Gif];

    public async Task<IReadOnlyDictionary<string, string>> ResolveAsync(BlogDbContext db, IReadOnlyCollection<string> sourceUrls, CancellationToken ct)
    {
        Dictionary<string, string> hashes = [with(StringComparer.Ordinal)];
        var urls = sourceUrls.Distinct(StringComparer.Ordinal).Where(u => SocialUrl.SafeHttps(u) is not null).ToList();
        if (urls.Count == 0)
            return hashes;

        // A known URL is never downloaded again.
        var known = await db.SocialAvatars.AsNoTracking().Where(a => urls.Contains(a.SourceUrl)).Select(a => new { a.SourceUrl, a.Hash }).ToListAsync(ct);
        foreach (var avatar in known)
            hashes[avatar.SourceUrl] = avatar.Hash;

        foreach (var url in urls.Where(u => !hashes.ContainsKey(u)))
        {
            ct.ThrowIfCancellationRequested();
            if (await TryFetchAsync(db, url, ct) is { } hash)
                hashes[url] = hash;
        }

        return hashes;
    }

    /// <summary>The hash of the stored avatar, or null: whatever went wrong, the thread goes on without this avatar.</summary>
    private async Task<string?> TryFetchAsync(BlogDbContext db, string url, CancellationToken ct)
    {
        try
        {
            var response = await SocialHttp.GetAsync(clients.CreateClient(SocialHttp.Avatars), url, SocialHttp.AvatarLimit, ct, hostOnlyTelemetry: true);
            if (!response.IsSuccess)
            {
                LogRejectedStatus(logger, (int)response.Status);
                return null;
            }

            var webp = Reencode(response.Body, out var rejection);
            if (webp is null)
            {
                LogRejected(logger, rejection ?? "not usable");
                return null;
            }

            return await StoreAsync(db, url, webp, ct);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            // The network, a slow or huge answer, a decoder fault, the disk: the reply shows the fallback avatar, and nothing says whose it was.
            var kind = ex.GetType().Name;
            LogFailed(logger, kind);
            return null;
        }
    }

    /// <summary>The 48 x 48 WebP of an image, or null with the reason (never a URL or a name).</summary>
    internal static byte[]? Reencode(byte[] source, out string? rejection)
    {
        rejection = null;
        using var data = SKData.CreateCopy(source);
        using var codec = SKCodec.Create(data);
        if (codec is null)
        {
            rejection = "not an image";
            return null;
        }

        if (!AllowedFormats.Contains(codec.EncodedFormat))
        {
            rejection = $"format {codec.EncodedFormat} is not allowed";
            return null;
        }

        // The dimensions come from the header: an image that claims to be huge is refused before any pixel is decoded.
        var (width, height) = (codec.Info.Width, codec.Info.Height);
        if (width is <= 0 or > MaxSide || height is <= 0 or > MaxSide)
        {
            rejection = $"dimensions {width}x{height} are out of range";
            return null;
        }

        using var bitmap = SKBitmap.Decode(codec);
        if (bitmap is null)
        {
            rejection = "could not be decoded";
            return null;
        }

        var side = Math.Min(bitmap.Width, bitmap.Height);
        var source2 = SKRect.Create((bitmap.Width - side) / 2f, (bitmap.Height - side) / 2f, side, side);
        using var surface = SKSurface.Create(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        using var image = SKImage.FromBitmap(bitmap);
        surface.Canvas.DrawImage(image, source2, SKRect.Create(0, 0, Size, Size), new SKSamplingOptions(SKCubicResampler.Mitchell), null);
        using var snapshot = surface.Snapshot();
        using var encoded = snapshot.Encode(SKEncodedImageFormat.Webp, 80);
        if (encoded is null)
        {
            rejection = "could not be encoded";
            return null;
        }

        return encoded.ToArray();
    }

    private async Task<string> StoreAsync(BlogDbContext db, string url, byte[] webp, CancellationToken ct)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(webp));
        if (await db.SocialAvatars.AnyAsync(a => a.Hash == hash, ct))
            return hash; // another URL showed the same image: one avatar, one file

        string path;
        await using (var stream = new MemoryStream(webp))
            path = await storage.SaveAsync(stream, $"{hash}.webp", "image/webp", Folder, ct);

        var avatar = new SocialAvatar { Hash = hash, SourceUrl = url, StoragePath = path, FetchedAt = time.GetUtcNow().UtcDateTime };
        db.SocialAvatars.Add(avatar);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Another replica stored the same image a moment ago: keep its row, drop the file this one wrote.
            db.Entry(avatar).State = EntityState.Detached;
            DeleteFile(path);
        }

        return hash;
    }

    public async Task SweepAsync(BlogDbContext db, CancellationToken ct)
    {
        var cutoff = time.GetUtcNow().UtcDateTime - OrphanGrace;
        var orphans = await db.SocialAvatars
            .Where(a => a.FetchedAt < cutoff && !db.SocialReplies.Any(r => r.AvatarHash == a.Hash))
            .ToListAsync(ct);
        if (orphans.Count == 0)
            return;

        db.SocialAvatars.RemoveRange(orphans);
        await db.SaveChangesAsync(ct);
        foreach (var orphan in orphans)
            DeleteFile(orphan.StoragePath);
    }

    private void DeleteFile(string path)
    {
        try
        {
            storage.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogFileNotDeleted(logger, ex.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "An avatar was not stored: the host answered HTTP {Status}.")]
    private static partial void LogRejectedStatus(ILogger logger, int status);

    [LoggerMessage(Level = LogLevel.Debug, Message = "An avatar was not stored: {Reason}.")]
    private static partial void LogRejected(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "An avatar could not be fetched ({ExceptionType}); its replies show the fallback.")]
    private static partial void LogFailed(ILogger logger, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "An unused avatar's file could not be deleted ({ExceptionType}).")]
    private static partial void LogFileNotDeleted(ILogger logger, string exceptionType);
}
