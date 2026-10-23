using Croft.Shared.Contracts.Social;

namespace Croft.Shared.Contracts;

/// <summary>Hashtags are the category's, in the order the owner gave them (empty when none, also when an older API omits the field).</summary>
public record CategoryDto(Guid Id, string Name, string Slug, string? Icon, int PostCount, IReadOnlyList<HashtagDto>? Hashtags = null)
{
    public IReadOnlyList<HashtagDto> Hashtags { get; init; } = Hashtags ?? [];

    // The synthesized equality would compare the lists by reference, so two copies of one category (one read back from JSON) would differ.
    public virtual bool Equals(CategoryDto? other) =>
        other is not null && Id == other.Id && Name == other.Name && Slug == other.Slug && Icon == other.Icon
        && PostCount == other.PostCount && Hashtags.SequenceEqual(other.Hashtags);

    public override int GetHashCode() => HashCode.Combine(Id, Name, Slug, Icon, PostCount, Hashtags.Count);
}
