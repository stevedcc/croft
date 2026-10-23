using Croft.Shared.Contracts.Social;

namespace Croft.Shared.Contracts;

/// <summary>Hashtags is optional: null (also when the field is absent) leaves the list unchanged; an empty list clears it.</summary>
public record UpdateCategoryRequest(string Name, string? Icon, IReadOnlyList<HashtagDto>? Hashtags = null);
