using Croft.Shared.Enums;

namespace Croft.Shared.Contracts;

/// <summary>One row of the anonymous pages listing (GET /api/pages), which the sitemap builds
/// from: a page's identity, its status (the same defensive published-only filter the post rows
/// get) and its <c>UpdatedAt</c> as the sitemap's <c>lastmod</c>.</summary>
public record PageSummaryDto(Guid Id, string Slug, string Title, PostStatus Status, DateTime UpdatedAt);
