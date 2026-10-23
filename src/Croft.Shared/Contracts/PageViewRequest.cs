namespace Croft.Shared.Contracts;

public record PageViewRequest(Guid PostId, string? Referrer = null);
