namespace Croft.Api.Services;

/// <summary>Outcome of one scheduled-publisher tick.</summary>
/// <param name="Skipped">True when another instance held the job lock, so this tick did nothing.</param>
/// <param name="Published">The posts this tick published, in no particular order.</param>
public sealed record PublishDuePostsResult(bool Skipped, IReadOnlyList<PublishedPost> Published);

/// <summary>A post the scheduled publisher published.</summary>
public sealed record PublishedPost(Guid Id, string Slug);
