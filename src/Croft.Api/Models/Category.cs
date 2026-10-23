namespace Croft.Api.Models;

public class Category
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Icon { get; set; }

    public ICollection<Post> Posts { get; set; } = [];
}
