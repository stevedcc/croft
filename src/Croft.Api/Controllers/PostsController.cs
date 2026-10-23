using Croft.Api.Identity;
using Croft.Shared.Contracts;
using Croft.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Croft.Api.Controllers;

[ApiController]
[Route("api/posts")]
public class PostsController(IPostService postService) : ControllerBase
{
    [HttpGet]
    [AllowAnonymous]
    public async Task<ActionResult<PagedResult<PostSummaryDto>>> List(
        [FromQuery] PostListRequest request,
        CancellationToken ct)
    {
        // The caller's role picks the variant unless the request asks for the public view (the
        // reading pages do): then every caller gets the public list. The flag can only narrow.
        var publicOnly = request.PublicView || (!User.IsInRole("Admin") && !User.IsInRole("Editor"));
        var includeAdminCounts = !request.PublicView && User.IsInRole("Admin");
        var result = await postService.ListAsync(request, publicOnly, includeAdminCounts, ct);
        return Ok(result);
    }

    [HttpGet("{slug}")]
    [AllowAnonymous]
    public async Task<ActionResult<PostDto>> Get(string slug, CancellationToken ct)
    {
        var publicOnly = !User.IsInRole("Admin") && !User.IsInRole("Editor");
        var post = await postService.GetBySlugAsync(slug, publicOnly, ct);
        return post is null ? NotFound() : Ok(post);
    }

    [HttpGet("{slug}/related")]
    [AllowAnonymous]
    public async Task<ActionResult<IReadOnlyList<PostSummaryDto>>> Related(string slug, [FromQuery] int count = 3, CancellationToken ct = default)
    {
        var publicOnly = !User.IsInRole("Admin") && !User.IsInRole("Editor");
        var includeAdminCounts = User.IsInRole("Admin");
        var related = await postService.RelatedAsync(slug, count, publicOnly, includeAdminCounts, ct);
        return related is null ? NotFound() : Ok(related);
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<PostDto>> Create(CreatePostRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        var userName = User.GetUserName();
        var post = await postService.CreateAsync(request, userId, userName, ct);
        return CreatedAtAction(nameof(Get), new { slug = post.Slug }, post);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin,Editor")]
    public async Task<ActionResult<PostDto>> Update(Guid id, UpdatePostRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        var isAdmin = User.IsInRole("Admin");
        var post = await postService.UpdateAsync(id, request, userId, isAdmin, ct);
        return post is null ? NotFound() : Ok(post);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var deleted = await postService.DeleteAsync(id, ct);
        return deleted ? NoContent() : NotFound();
    }

    [HttpPost("{id:guid}/publish")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<PostDto>> Publish(Guid id, PublishRequest request, CancellationToken ct)
    {
        var post = await postService.PublishAsync(id, request, ct);
        return post is null ? NotFound() : Ok(post);
    }

    [HttpPost("{id:guid}/unpublish")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<PostDto>> Unpublish(Guid id, CancellationToken ct)
    {
        var post = await postService.UnpublishAsync(id, ct);
        return post is null ? NotFound() : Ok(post);
    }
}
