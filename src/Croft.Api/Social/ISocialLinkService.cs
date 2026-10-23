using Croft.Api.Models;

namespace Croft.Api.Social;

/// <summary>The discussion links of a post after validation and resolution: what is stored. All null: no link. An <c>Unchanged</c> flag means the request did not mention that platform: its stored link stays as it is.</summary>
public sealed record ResolvedSocialLinks(
    string? MastodonUrl, string? MastodonStatusId, string? BlueskyUrl, string? BlueskyUri,
    bool MastodonUnchanged = false, bool BlueskyUnchanged = false);

/// <summary>
/// Validates and applies a post's two discussion links (brief 12a). Two steps so that a refused link
/// saves nothing: <see cref="ResolveAsync"/> only validates (and may call Bluesky to resolve a handle), then
/// <see cref="ApplyAsync"/> changes the tracked post and its thread rows, to be saved with the post's own save.
/// </summary>
public interface ISocialLinkService
{
    /// <summary>
    /// Validates the URLs (blank = no link; null = no link, or with <paramref name="nullMeansUnchanged"/> "leave that platform as it is") and resolves a Bluesky handle to its DID. Throws
    /// <see cref="Errors.ValidationException"/> naming the field, or <see cref="Errors.UpstreamUnavailableException"/> when
    /// the handle cannot be resolved right now.
    /// </summary>
    Task<ResolvedSocialLinks> ResolveAsync(string? mastodonUrl, string? blueskyUrl, bool nullMeansUnchanged = false, CancellationToken ct = default);

    /// <summary>
    /// Sets the links on <paramref name="post"/> (new or tracked) and keeps its thread rows in step: a new or changed link
    /// stamps <see cref="Post.DiscussionLinkedAt"/> and gives the platform a fresh thread, due now; a cleared link
    /// removes the platform's thread and replies. Nothing is saved here. Returns true when a link was set, changed or cleared
    /// (the discussion cache must then be invalidated).
    /// </summary>
    Task<bool> ApplyAsync(Post post, ResolvedSocialLinks links, DateTime now, CancellationToken ct = default);
}
