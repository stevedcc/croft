using System.Net;
using System.Text;
using Croft.Shared.Contracts.Social;

namespace Croft.Api.Social;

/// <summary>Asks Bluesky's public service for a post thread (<c>app.bsky.feed.getPostThread</c>, depth 10, no parents, no credentials).</summary>
public sealed class BlueskyThreadSource(IHttpClientFactory clients, TimeProvider time) : ISocialThreadSource
{
    public const string Endpoint = "https://public.api.bsky.app/xrpc/app.bsky.feed.getPostThread";

    public SocialPlatform Platform => SocialPlatform.Bluesky;

    public async Task<SocialFetchOutcome> FetchAsync(SocialClaim claim, SocialBlockSet blocks, CancellationToken ct)
    {
        SocialResponse response;
        try
        {
            response = await SocialHttp.GetAsync(clients.CreateClient(SocialHttp.Bluesky),
                $"{Endpoint}?uri={Uri.EscapeDataString(claim.RemoteId)}&depth=10&parentHeight=0", SocialHttp.ThreadLimit, ct);
        }
        catch (Exception ex) when (SocialFetchErrors.IsExpected(ex, ct))
        {
            return SocialFetchErrors.From("Bluesky", ex);
        }

        if (response.Status == HttpStatusCode.TooManyRequests)
            return new SocialFetchOutcome.RateLimited(SocialRateLimit.RetryAt(response.Headers, time.GetUtcNow().UtcDateTime));

        if (!response.IsSuccess)
            return new SocialFetchOutcome.Failed($"Bluesky answered HTTP {(int)response.Status}");

        try
        {
            return new SocialFetchOutcome.Fetched(BlueskyThreadParser.Parse(Encoding.UTF8.GetString(response.Body), blocks));
        }
        catch (SocialParseException)
        {
            return new SocialFetchOutcome.Failed("Bluesky sent an unexpected answer");
        }
    }
}
