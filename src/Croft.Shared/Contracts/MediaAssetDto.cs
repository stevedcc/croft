using Croft.Shared.Enums;

namespace Croft.Shared.Contracts;

public record MediaAssetDto(Guid Id, string FileName, string OriginalName, string ContentType, string Url,
    MediaAssetScope Scope, Guid? PostId, string? AltText);
