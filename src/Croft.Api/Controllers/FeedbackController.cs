using Croft.Api.Identity;
using Croft.Api.Services;
using Croft.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Croft.Api.Controllers;

[ApiController]
[Route("api/posts/{postId:guid}/feedback")]
public class FeedbackController(IFeedbackService feedbackService) : ControllerBase
{
    [HttpGet]
    [Authorize(Roles = "Admin,Editor")]
    public async Task<IActionResult> List(Guid postId, CancellationToken ct)
    {
        var feedback = await feedbackService.ListForPostAsync(postId, ct);
        return Ok(feedback);
    }

    [HttpPost]
    [Authorize(Roles = "Editor,Admin")]
    public async Task<IActionResult> Create(Guid postId, CreateFeedbackRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        var userName = User.GetUserName();
        var feedback = await feedbackService.CreateAsync(postId, userId, userName, request.QuotedText, request.Comment, ct);
        if (feedback is null)
            return NotFound();
        return CreatedAtAction(nameof(List), new { postId }, feedback);
    }

    [HttpDelete("{feedbackId:guid}")]
    [Authorize(Roles = "Editor,Admin")]
    public async Task<IActionResult> Delete(Guid postId, Guid feedbackId, CancellationToken ct)
    {
        var userId = User.GetUserId();
        var isAdmin = User.IsInRole("Admin");
        var deleted = await feedbackService.DeleteAsync(postId, feedbackId, userId, isAdmin, ct);
        return deleted ? NoContent() : NotFound();
    }

    [HttpPost("{feedbackId:guid}/resolve")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Resolve(Guid postId, Guid feedbackId, CancellationToken ct)
    {
        var feedback = await feedbackService.ResolveAsync(postId, feedbackId, ct);
        return feedback is null ? NotFound() : Ok(feedback);
    }
}
