using Croft.Shared.Contracts.Social;

namespace Croft.Api.Social;

/// <summary>
/// A thread a replica has claimed to fetch: which post, which platform, the remote id to ask for (the Mastodon status id or the
/// Bluesky at:// URI) and the schedule's anchor (the later of the post's publication and its link: how old the discussion is).
/// </summary>
public sealed record SocialClaim(Guid PostId, SocialPlatform Platform, string RemoteId, DateTime Anchor);

/// <summary>What asking a platform for a thread came to.</summary>
public closed record SocialFetchOutcome
{
    /// <summary>The thread, filtered; <c>Counts</c> are the platform's own counts of the announcement, null when the answer had none. <c>PlatformBusyUntil</c> is set when the answer itself said the rate limit is used up: no further request before then.</summary>
    public sealed record Fetched(IReadOnlyList<IngestedReply> Replies, DateTime? PlatformBusyUntil = null, SocialCounts? Counts = null) : SocialFetchOutcome;

    /// <summary>The platform answered 429: no further request to it before <c>Until</c>.</summary>
    public sealed record RateLimited(DateTime Until) : SocialFetchOutcome;

    /// <summary>The fetch failed; <c>Error</c> is a short, privacy-free description (a kind and a status, never a handle or a text).</summary>
    public sealed record Failed(string Error) : SocialFetchOutcome;
}

/// <summary>One platform's way of fetching and filtering a thread.</summary>
public interface ISocialThreadSource
{
    SocialPlatform Platform { get; }

    Task<SocialFetchOutcome> FetchAsync(SocialClaim claim, SocialBlockSet blocks, CancellationToken ct);
}
