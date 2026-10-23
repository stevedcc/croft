namespace Croft.Api.Models;

public class Feedback
{
    public Guid Id { get; set; }
    public Guid PostId { get; set; }
    public Post Post { get; set; } = null!;
    public string AuthorId { get; set; } = string.Empty;
    public string AuthorName { get; set; } = string.Empty;
    public string QuotedText { get; set; } = string.Empty;
    public string Comment { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
}
