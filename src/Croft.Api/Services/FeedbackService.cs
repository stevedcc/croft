using Croft.Api.Data;
using Croft.Api.Errors;
using Croft.Api.Models;
using Croft.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Croft.Api.Services;

public class FeedbackService(BlogDbContext db, TimeProvider time) : IFeedbackService
{
    /// <summary>A quote is a selection from one post: a paragraph or two. The editor's quote box carries the same limit.</summary>
    public const int MaxQuotedTextLength = 2_000;

    /// <summary>A comment is a note, not a document. The editor's comment box carries the same limit.</summary>
    public const int MaxCommentLength = 10_000;

    public async Task<FeedbackDto?> CreateAsync(Guid postId, string authorId, string authorName, string quotedText, string comment, CancellationToken ct = default)
    {
        if (quotedText.Length > MaxQuotedTextLength)
            throw new ValidationException($"Quote must be {MaxQuotedTextLength:N0} characters or fewer.");

        if (comment.Length > MaxCommentLength)
            throw new ValidationException($"Comment must be {MaxCommentLength:N0} characters or fewer.");

        var postExists = await db.Posts.AnyAsync(p => p.Id == postId, ct);
        if (!postExists)
            return null;

        var feedback = new Feedback
        {
            PostId = postId,
            AuthorId = authorId,
            AuthorName = authorName,
            QuotedText = quotedText,
            Comment = comment,
            CreatedAt = time.GetUtcNow().UtcDateTime
        };

        db.Feedback.Add(feedback);
        await db.SaveChangesAsync(ct);

        return MapToDto(feedback);
    }

    public async Task<IReadOnlyList<FeedbackDto>> ListForPostAsync(Guid postId, CancellationToken ct = default)
    {
        var feedback = await db.Feedback
            .AsNoTracking()
            .Where(f => f.PostId == postId)
            .OrderByDescending(f => f.CreatedAt)
            .ToListAsync(ct);

        return [.. feedback.Select(MapToDto)];
    }

    public async Task<bool> DeleteAsync(Guid postId, Guid feedbackId, string userId, bool isAdmin, CancellationToken ct = default)
    {
        // Matched on the post too (as ResolveAsync does): another post's URL is a missing id.
        var feedback = await db.Feedback.FirstOrDefaultAsync(f => f.Id == feedbackId && f.PostId == postId, ct);
        if (feedback is null)
            return false;

        if (!isAdmin && feedback.AuthorId != userId)
            return false;

        db.Feedback.Remove(feedback);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<FeedbackDto?> ResolveAsync(Guid postId, Guid feedbackId, CancellationToken ct = default)
    {
        var feedback = await db.Feedback
            .FirstOrDefaultAsync(f => f.Id == feedbackId && f.PostId == postId, ct);
        if (feedback is null)
            return null;

        feedback.ResolvedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);

        return MapToDto(feedback);
    }

    private static FeedbackDto MapToDto(Feedback feedback)
    {
        return new FeedbackDto(
            feedback.Id,
            feedback.PostId,
            feedback.AuthorId,
            feedback.AuthorName,
            feedback.QuotedText,
            feedback.Comment,
            feedback.CreatedAt,
            feedback.ResolvedAt);
    }
}
