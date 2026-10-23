using System.Net;
using Croft.Api.Services;
using Croft.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Croft.Api.Controllers;

[ApiController]
[Route("api/analytics")]
public class AnalyticsController(IAnalyticsService analyticsService, IConfiguration configuration) : ControllerBase
{
    private readonly string? _ingestKey = configuration["Analytics:IngestKey"];

    [HttpPost("pageview")]
    [AllowAnonymous]
    public async Task<IActionResult> RecordPageView(PageViewRequest request, CancellationToken ct)
    {
        var ip = ResolveVisitorIp();
        var userAgent = Request.Headers.UserAgent.ToString();
        await analyticsService.RecordPageViewAsync(request.PostId, ip, userAgent, request.Referrer, ct);
        return NoContent();
    }

    private string? ResolveVisitorIp()
    {
        // The Web app forwards the browser's address in X-Visitor-IP. Honour it only when the
        // request carries the shared ingest key and the value is an IP address (anything else is
        // ignored, never stored), otherwise fall back to the socket peer. The API handles no
        // X-Forwarded-* headers: this is its only visitor override (brief 13b).
        var forwardedIp = Request.Headers["X-Visitor-IP"].FirstOrDefault();
        var providedKey = Request.Headers["X-Analytics-Key"].FirstOrDefault();
        if (IPAddress.TryParse(forwardedIp, out var visitor) && AnalyticsIngestKey.Matches(providedKey, _ingestKey))
            return visitor.ToString();

        return HttpContext.Connection.RemoteIpAddress?.ToString();
    }

    [HttpGet]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetSummary(
        [FromQuery] Guid postId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken ct)
    {
        var summary = await analyticsService.GetSummaryAsync(postId, from, to, ct);
        return Ok(summary);
    }
}
