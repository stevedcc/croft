using Croft.Api.Data;
using Croft.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Croft.Api.Controllers;

/// <summary>The stored 48 x 48 avatars of social replies (brief 12a). The Web proxies this at <c>/social/avatar/{hash}.webp</c>.</summary>
[ApiController]
public class SocialAvatarsController(BlogDbContext db, IFileStorage storage) : ControllerBase
{
    /// <summary>The year in seconds: the URL carries the hash of the bytes, so the content never changes.</summary>
    private const string CacheControl = "public, max-age=31536000, immutable";

    [HttpGet("api/social/avatars/{hash}.webp")]
    [AllowAnonymous]
    public async Task<IActionResult> Get(string hash, CancellationToken ct)
    {
        // Exactly the 64 lower-case hex characters of a SHA-256: anything else is not a hash, and is not looked up.
        if (hash.Length != 64 || !hash.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
            return NotFound();

        var path = await db.SocialAvatars.AsNoTracking().Where(a => a.Hash == hash).Select(a => a.StoragePath).FirstOrDefaultAsync(ct);
        if (path is null)
            return NotFound();

        Stream stream;
        try
        {
            stream = storage.OpenRead(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = CacheControl;
        return File(stream, "image/webp");
    }
}
