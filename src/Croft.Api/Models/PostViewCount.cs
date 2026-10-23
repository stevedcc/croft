namespace Croft.Api.Models;

/// <summary>
/// A post's view total, in a narrow table of its own (brief 13c item 4, architecture finding 4):
/// counting a view upserts this row and never touches the wide, heavily indexed Posts row. A post
/// with no row has no counted views.
/// </summary>
public class PostViewCount
{
    public Guid PostId { get; set; }
    public Post Post { get; set; } = null!;
    public long ViewCount { get; set; }
}
