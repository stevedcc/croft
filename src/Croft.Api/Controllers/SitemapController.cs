using Croft.Api.Services;
using Croft.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Croft.Api.Controllers;

[ApiController]
[Route("api/sitemap")]
public class SitemapController(ISitemapQueryService sitemap) : ControllerBase
{
    /// <summary>What the Web's sitemap.xml is built from: published posts and pages (slug and
    /// <c>UpdatedAt</c>) and the slugs of the categories and tags that have a published post.
    /// The same for every caller.</summary>
    [HttpGet]
    [AllowAnonymous]
    public async Task<ActionResult<SitemapDto>> Get(CancellationToken ct) => Ok(await sitemap.GetAsync(ct));
}
