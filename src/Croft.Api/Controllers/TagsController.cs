using Croft.Api.Services;
using Croft.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Croft.Api.Controllers;

[ApiController]
[Route("api/tags")]
public class TagsController(ITagService tagService) : ControllerBase
{
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> List(int? page, int? pageSize, string? q, [FromQuery] string[]? names, CancellationToken ct)
    {
        if (names is { Length: > 0 })
            return Ok(await tagService.ListByNamesAsync(page ?? 1, pageSize ?? 10, names, ct));

        // With any of page/pageSize/q this is the paged contract (PagedResult, like the posts
        // list); without them the legacy full array (seed scripts and raw callers parse it).
        if (page.HasValue || pageSize.HasValue || q is not null)
            return Ok(await tagService.ListAsync(page ?? 1, pageSize ?? 10, q, ct));

        var tags = await tagService.ListAsync(ct);
        return Ok(tags);
    }

    [HttpGet("{slug}")]
    [AllowAnonymous]
    public async Task<IActionResult> Get(string slug, CancellationToken ct)
    {
        var tag = await tagService.GetBySlugAsync(slug, ct);
        return tag is null ? NotFound() : Ok(tag);
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create([FromBody] string name, CancellationToken ct)
    {
        var tag = await tagService.CreateAsync(name, ct);
        return CreatedAtAction(nameof(List), tag);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(Guid id, UpdateTagRequest request, CancellationToken ct)
    {
        var tag = await tagService.UpdateAsync(id, request, ct);
        return tag is null ? NotFound() : Ok(tag);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var deleted = await tagService.DeleteAsync(id, ct);
        return deleted ? NoContent() : NotFound();
    }
}
