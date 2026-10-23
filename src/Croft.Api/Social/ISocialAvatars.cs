using Croft.Api.Data;

namespace Croft.Api.Social;

/// <summary>Avatars of replies (brief 12a): downloaded once, re-encoded, stored under a content hash; an avatar nobody references is deleted.</summary>
public interface ISocialAvatars
{
    /// <summary>The hash of each avatar that could be fetched (or was already known), by source URL. A URL that failed is absent: that reply shows the fallback.</summary>
    Task<IReadOnlyDictionary<string, string>> ResolveAsync(BlogDbContext db, IReadOnlyCollection<string> sourceUrls, CancellationToken ct);

    /// <summary>Deletes the avatar rows and files no stored reply references.</summary>
    Task SweepAsync(BlogDbContext db, CancellationToken ct);
}
