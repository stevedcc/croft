using Croft.Shared.Contracts;

namespace Croft.Api.Services;

public interface IFeedbackService
{
    Task<FeedbackDto?> CreateAsync(Guid postId, string authorId, string authorName, string quotedText, string comment, CancellationToken ct = default);
    Task<IReadOnlyList<FeedbackDto>> ListForPostAsync(Guid postId, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid postId, Guid feedbackId, string userId, bool isAdmin, CancellationToken ct = default);
    Task<FeedbackDto?> ResolveAsync(Guid postId, Guid feedbackId, CancellationToken ct = default);
}
