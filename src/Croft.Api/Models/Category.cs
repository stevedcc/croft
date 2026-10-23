using Croft.Shared.Contracts.Social;

namespace Croft.Api.Models;

public class Category
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Icon { get; set; }

    /// <summary>The hashtags the default announcement may append, in the owner's order; one nullable jsonb column, null reads as empty.</summary>
    // WORKAROUND(W-101, EF Core): EF hands the setter null for a NULL column; it never converts NULL
    public List<HashtagDto> Hashtags { get; set => field = value ?? []; } = [];
    // END WORKAROUND(W-101)

    public ICollection<Post> Posts { get; set; } = [];
}
