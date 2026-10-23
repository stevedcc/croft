using Croft.Api.Social;
using Croft.Shared.Contracts.Social;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Croft.Api.Controllers;

/// <summary>A post's announcements on Mastodon and Bluesky (brief 12d): the editor's status block and its "Announce now" / "Retry" buttons.</summary>
[ApiController]
public class AnnouncementsController(IAnnouncementService announcements) : ControllerBase
{
    [HttpGet("api/posts/{id:guid}/announcements")]
    [Authorize(Roles = "Admin,Editor")]
    public async Task<ActionResult<PostAnnouncementsDto>> Get(Guid id, CancellationToken ct)
    {
        var result = await announcements.GetAsync(id, ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPost("api/posts/{id:guid}/announcements/{platform}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<AnnouncementStatusDto>> Announce(Guid id, SocialPlatform platform, CancellationToken ct)
    {
        if (!Enum.IsDefined(platform))
            return BadRequest();

        var result = await announcements.AnnounceAsync(id, platform, ct);
        return result is null ? NotFound() : Accepted(result);
    }
}
