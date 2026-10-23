using Croft.Shared.Enums;

namespace Croft.Shared.Contracts;

/// <summary>A post list query. <paramref name="PublicView"/> asks for the public list whoever is
/// signed in (published posts only, no admin counts): the reading pages set it, so an admin
/// sees what visitors see and is served from the public read cache; the admin grid leaves it off.</summary>
public record PostListRequest(
    string? SearchTerm = null,
    string? TagSlug = null,
    string? CategorySlug = null,
    PostStatus? Status = null,
    PostKind? Kind = null,
    int Page = 1,
    int PageSize = 10,
    bool PublicView = false);
