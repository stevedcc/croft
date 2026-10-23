namespace Croft.Shared.Contracts;

/// <summary>
/// CountsTowardsRelated is optional: null (also when the field is absent — a Web of the previous
/// release) leaves the tag's current value unchanged.
/// </summary>
public record UpdateTagRequest(string Name, bool? CountsTowardsRelated = null);
