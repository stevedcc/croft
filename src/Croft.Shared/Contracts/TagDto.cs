using Croft.Shared.Contracts.Social;

namespace Croft.Shared.Contracts;

/// <summary>
/// PostCount is the number of published posts carrying the tag (same rule as <see cref="CategoryDto"/>.PostCount).
/// CountsTowardsRelated is the tag's "Counts towards related posts" switch: when off the tag is
/// neutral in the related-posts ranking — it contributes no points and no demotion.
/// Hashtags are the tag's, in the order the owner gave them (empty when none, also when an older API omits the field).
/// </summary>
public record TagDto(Guid Id, string Name, string Slug, int PostCount, bool CountsTowardsRelated, IReadOnlyList<HashtagDto>? Hashtags = null)
{
    public IReadOnlyList<HashtagDto> Hashtags { get; init; } = Hashtags ?? [];

    // The synthesized equality would compare the lists by reference, so two copies of one tag (one read back from JSON) would differ.
    public virtual bool Equals(TagDto? other) =>
        other is not null && Id == other.Id && Name == other.Name && Slug == other.Slug && PostCount == other.PostCount
        && CountsTowardsRelated == other.CountsTowardsRelated && Hashtags.SequenceEqual(other.Hashtags);

    public override int GetHashCode() => HashCode.Combine(Id, Name, Slug, PostCount, CountsTowardsRelated, Hashtags.Count);
}
