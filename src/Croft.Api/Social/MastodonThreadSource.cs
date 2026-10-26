using System.Net;
using System.Text;
using Croft.Shared.Contracts.Social;
using Microsoft.Extensions.Options;

namespace Croft.Api.Social;

/// <summary>
/// Asks the owner's Mastodon server for a status's context (<c>GET /api/v1/statuses/{id}/context</c>, no credentials in 12a) and then for the status itself (its counts, brief 17e), honouring its
/// rate limit: a 429 and an answer with <c>X-RateLimit-Remaining: 0</c> both tell the fetcher to stay away until the reset.
/// </summary>
public sealed class MastodonThreadSource(IHttpClientFactory clients, IOptions<SocialOptions> options, TimeProvider time) : ISocialThreadSource
{
    public SocialPlatform Platform => SocialPlatform.Mastodon;

    public async Task<SocialFetchOutcome> FetchAsync(SocialClaim claim, SocialBlockSet blocks, CancellationToken ct)
    {
        var server = options.Value.Mastodon.Server.Trim();
        if (server.Length == 0)
            return new SocialFetchOutcome.Failed("Mastodon is not configured");

        // The id comes from a validated link, and is checked again before it goes into a URL.
        if (claim.RemoteId.Length == 0 || !claim.RemoteId.All(char.IsAsciiDigit))
            return new SocialFetchOutcome.Failed("Mastodon status id is not valid");

        SocialResponse response;
        try
        {
            response = await SocialHttp.GetAsync(clients.CreateClient(SocialHttp.Mastodon),
                $"https://{server}/api/v1/statuses/{claim.RemoteId}/context", SocialHttp.ThreadLimit, ct);
        }
        catch (Exception ex) when (SocialFetchErrors.IsExpected(ex, ct))
        {
            return SocialFetchErrors.From("Mastodon", ex);
        }

        var now = time.GetUtcNow().UtcDateTime;
        if (response.Status == HttpStatusCode.TooManyRequests)
            return new SocialFetchOutcome.RateLimited(SocialRateLimit.RetryAt(response.Headers, now));

        if (!response.IsSuccess)
            return new SocialFetchOutcome.Failed($"Mastodon answered HTTP {(int)response.Status}");

        try
        {
            var replies = MastodonThreadParser.Parse(Encoding.UTF8.GetString(response.Body), claim.RemoteId, server, blocks);
            var busyUntil = SocialRateLimit.BusyUntil(response.Headers, now);

            // The counts come from the status itself; they are an extra, so no failure of this request fails the fetch.
            var (counts, statusBusyUntil) = await FetchCountsAsync(server, claim.RemoteId, now, ct);
            if (statusBusyUntil is { } until && (busyUntil is not { } current || until > current))
                busyUntil = until;

            return new SocialFetchOutcome.Fetched(replies, busyUntil, counts);
        }
        catch (SocialParseException)
        {
            return new SocialFetchOutcome.Failed("Mastodon sent an unexpected answer");
        }
    }

    /// <summary>
    /// <c>GET /api/v1/statuses/{id}</c> for <c>favourites_count</c>, <c>reblogs_count</c> and <c>replies_count</c>. Any failure (network, timeout, a
    /// non-2xx answer, a body without the counts) yields no counts. The rate limit is shared with the context request, so a 429's
    /// retry time, or an answer that used the limit up, still marks the platform busy.
    /// </summary>
    private async Task<(SocialCounts? Counts, DateTime? BusyUntil)> FetchCountsAsync(string server, string statusId, DateTime now, CancellationToken ct)
    {
        SocialResponse response;
        try
        {
            response = await SocialHttp.GetAsync(clients.CreateClient(SocialHttp.Mastodon),
                $"https://{server}/api/v1/statuses/{statusId}", SocialHttp.ThreadLimit, ct);
        }
        catch (Exception ex) when (SocialFetchErrors.IsExpected(ex, ct))
        {
            return (null, null);
        }

        if (response.Status == HttpStatusCode.TooManyRequests)
            return (null, SocialRateLimit.RetryAt(response.Headers, now));

        return response.IsSuccess
            ? (MastodonThreadParser.ParseCounts(Encoding.UTF8.GetString(response.Body)), SocialRateLimit.BusyUntil(response.Headers, now))
            : (null, null);
    }
}
