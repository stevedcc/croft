namespace Croft.Api.Models;

public class PageView
{
    public Guid Id { get; set; }
    public Guid PostId { get; set; }
    public Post Post { get; set; } = null!;
    public DateTime Timestamp { get; set; }
    public DateOnly Day { get; set; }
    public string IpHash { get; set; } = string.Empty;
    public string? UserAgent { get; set; }
    public string? Referrer { get; set; }
}
