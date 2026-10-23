using Croft.Shared.Contracts.Social;

namespace Croft.Shared.Contracts;

/// <summary>
/// CountsTowardsRelated is optional: null (also when the field is absent — a Web of the previous
/// release) leaves the tag's current value unchanged. Hashtags is optional the same way: null leaves the
/// list unchanged; an empty list clears it.
/// </summary>
public record UpdateTagRequest(string Name, bool? CountsTowardsRelated = null, IReadOnlyList<HashtagDto>? Hashtags = null);
