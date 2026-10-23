namespace Croft.Api.Models;

public class Tag
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;

    /// <summary>The "Counts towards related posts" switch; new tags count.</summary>
    public bool CountsTowardsRelated { get; set; } = true;

    public ICollection<Post> Posts { get; set; } = [];
}
