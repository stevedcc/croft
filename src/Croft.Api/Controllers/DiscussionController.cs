using Croft.Api.Identity;
using Croft.Api.Social;
using Croft.Shared.Contracts.Social;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Croft.Api.Controllers;

/// <summary>The discussion below a post (brief 12a): the public read, the staff read and refresh, and the blocklist.</summary>
[ApiController]
public class DiscussionController(IDiscussionService discussion) : ControllerBase
{
    [HttpGet("api/posts/{slug}/discussion")]
    [AllowAnonymous]
    public async Task<ActionResult<PostDiscussionDto>> Get(string slug, CancellationToken ct)
    {
        var result = await discussion.GetPublicAsync(slug, ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet("api/posts/{id:guid}/discussion/admin")]
    [Authorize(Roles = "Admin,Editor")]
    public async Task<ActionResult<PostDiscussionAdminDto>> GetAdmin(Guid id, CancellationToken ct)
    {
        var result = await discussion.GetAdminAsync(id, ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPost("api/posts/{id:guid}/discussion/refresh")]
    [Authorize(Roles = "Admin,Editor")]
    public async Task<IActionResult> Refresh(Guid id, CancellationToken ct) =>
        await discussion.RefreshAsync(id, ct) ? Accepted() : NotFound();

    [HttpGet("api/discussion/blocks")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<IReadOnlyList<SocialBlockDto>>> ListBlocks(CancellationToken ct) =>
        Ok(await discussion.ListBlocksAsync(ct));

    [HttpPost("api/discussion/blocks")]
    [Authorize(Roles = "Admin,Editor")]
    public async Task<ActionResult<SocialBlockDto>> CreateBlock(CreateSocialBlockRequest request, CancellationToken ct)
    {
        var result = await discussion.CreateBlockAsync(request, User.GetUserName(), ct);
        return result.Created
            ? Created($"/api/discussion/blocks/{result.Block.Id}", result.Block)
            : Ok(result.Block);
    }

    [HttpDelete("api/discussion/blocks/{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> DeleteBlock(Guid id, CancellationToken ct) =>
        await discussion.DeleteBlockAsync(id, ct) ? NoContent() : NotFound();
}
