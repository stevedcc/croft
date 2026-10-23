namespace Croft.Shared.Contracts;

public record CategoryDto(Guid Id, string Name, string Slug, string? Icon, int PostCount);
