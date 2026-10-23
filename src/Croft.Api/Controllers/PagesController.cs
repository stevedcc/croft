using Croft.Shared.Contracts;
using Croft.Api.Services;
using Croft.Shared.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Croft.Api.Controllers;

[ApiController]
[Route("api/pages")]
public class PagesController(IPostService postService) : ControllerBase
{
    /// <summary>The sitemap's anonymous read: every published page with its UpdatedAt (lastmod),
    /// paged like the other list endpoints. Published-only — pages are never part of the posts
    /// listings, so this is where public page rows come from.</summary>
    [HttpGet]
    [AllowAnonymous]
    public async Task<ActionResult<PagedResult<PageSummaryDto>>> List(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 100, CancellationToken ct = default)
    {
        return Ok(await postService.ListPagesAsync(page, pageSize, ct));
    }

    /// <summary>The anonymous read of one navigation list: published pages with the given
    /// placement, <c>Sidebar</c> (the default) or <c>Footer</c>. <c>None</c> is not a list: 400.</summary>
    [HttpGet("navigation")]
    [AllowAnonymous]
    public async Task<ActionResult<IReadOnlyList<PageNavItemDto>>> Navigation(
        [FromQuery] NavigationPlacement placement = NavigationPlacement.Sidebar, CancellationToken ct = default)
    {
        if (placement == NavigationPlacement.None)
            return BadRequest("placement must be Sidebar or Footer.");

        return Ok(await postService.GetPageNavigationAsync(placement, ct));
    }

    /// <summary>Both navigation lists in one anonymous read: what a reading page's shell needs.</summary>
    [HttpGet("navigation/all")]
    [AllowAnonymous]
    public async Task<ActionResult<PageNavigationDto>> NavigationAll(CancellationToken ct)
    {
        return Ok(await postService.GetPageNavigationsAsync(ct));
    }

    /// <summary>The public page fetch: published pages only (404 otherwise), drafts for signed-in
    /// editors — the same visibility boundary as GET /api/posts/{slug}.</summary>
    [HttpGet("{slug}")]
    [AllowAnonymous]
    public async Task<ActionResult<PostDto>> Get(string slug, CancellationToken ct)
    {
        var publicOnly = !User.IsInRole("Admin") && !User.IsInRole("Editor");
        var page = await postService.GetPageBySlugAsync(slug, publicOnly, ct);
        return page is null ? NotFound() : Ok(page);
    }
}
