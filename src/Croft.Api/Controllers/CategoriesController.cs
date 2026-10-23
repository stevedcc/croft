using Croft.Api.Services;
using Croft.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Croft.Api.Controllers;

[ApiController]
[Route("api/categories")]
public class CategoriesController(ICategoryService categoryService) : ControllerBase
{
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> List(int? page, int? pageSize, string? q, int? top, CancellationToken ct)
    {
        // With any of page/pageSize/q this is the paged contract (PagedResult, like the posts
        // list); with only top it is the sidebar's bounded read; with neither, the legacy full
        // array (seed scripts and raw callers parse it).
        if (page.HasValue || pageSize.HasValue || q is not null)
            return Ok(await categoryService.ListAsync(page ?? 1, pageSize ?? 10, q, ct));

        if (top.HasValue)
            return Ok(await categoryService.TopAsync(top.Value, ct));

        var categories = await categoryService.ListAsync(ct);
        return Ok(categories);
    }

    [HttpGet("{slug}")]
    [AllowAnonymous]
    public async Task<IActionResult> Get(string slug, CancellationToken ct)
    {
        var category = await categoryService.GetBySlugAsync(slug, ct);
        return category is null ? NotFound() : Ok(category);
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create([FromBody] string name, CancellationToken ct)
    {
        var category = await categoryService.CreateAsync(name, ct);
        return CreatedAtAction(nameof(List), category);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(Guid id, UpdateCategoryRequest request, CancellationToken ct)
    {
        var category = await categoryService.UpdateAsync(id, request, ct);
        return category is null ? NotFound() : Ok(category);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var deleted = await categoryService.DeleteAsync(id, ct);
        return deleted ? NoContent() : NotFound();
    }
}
