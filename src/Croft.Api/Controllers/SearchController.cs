using Croft.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Croft.Api.Controllers;

[ApiController]
[Route("api/search")]
public class SearchController(ISearchService searchService) : ControllerBase
{
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Search([FromQuery] string q, [FromQuery] int page = 1, [FromQuery] int pageSize = 10, [FromQuery] bool publicView = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest(new { error = "Query is required." });

        var includeAdminCounts = !publicView && User.IsInRole("Admin");
        var result = await searchService.SearchAsync(q, includeAdminCounts, page, pageSize, ct);
        return Ok(result);
    }
}
