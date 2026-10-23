namespace Croft.Shared.Contracts;

public record FeedbackDto(
    Guid Id,
    Guid PostId,
    string AuthorId,
    string AuthorName,
    string QuotedText,
    string Comment,
    DateTime CreatedAt,
    DateTime? ResolvedAt);
