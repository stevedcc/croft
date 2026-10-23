namespace Croft.Shared.Contracts;

/// <summary>
/// PostCount is the number of published posts carrying the tag (same rule as <see cref="CategoryDto"/>.PostCount).
/// CountsTowardsRelated is the tag's "Counts towards related posts" switch: when off the tag is
/// neutral in the related-posts ranking — it contributes no points and no demotion.
/// </summary>
public record TagDto(Guid Id, string Name, string Slug, int PostCount, bool CountsTowardsRelated);
