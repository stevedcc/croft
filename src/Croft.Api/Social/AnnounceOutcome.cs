namespace Croft.Api.Social;

/// <summary>What asking a network about an announcement came to (brief 12d).</summary>
public abstract record AnnounceOutcome
{
    /// <summary>The announcement is on the network (just sent, or found by the look for an earlier send).</summary>
    public sealed record Posted(string RemoteId, string RemoteUrl) : AnnounceOutcome;

    /// <summary>The look for an earlier send found nothing.</summary>
    public sealed record NotFound : AnnounceOutcome;

    /// <summary>Try again later: a timeout, a network error, a busy server, a rate limit (<c>NotBefore</c> is when it says to come back, if it says).</summary>
    public sealed record Retry(string Reason, DateTime? NotBefore) : AnnounceOutcome;

    /// <summary>An error that will not heal by waiting (bad credentials, a refused post). <c>Reason</c> carries no secret.</summary>
    public sealed record Failed(string Reason) : AnnounceOutcome;
}
