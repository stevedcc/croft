using System.Net;
using System.Text.Json;
using Croft.Api.Errors;

namespace Croft.Api.Social;

/// <summary>
/// Resolves a handle with <c>com.atproto.identity.resolveHandle</c> on Bluesky's public service (brief 12a), once, when a post is saved.
/// A handle the service does not know is the owner's mistake (400); a service that does not answer is an outage (503), never a silent save.
/// </summary>
public sealed class BlueskyHandleResolver(IHttpClientFactory clients) : IBlueskyHandleResolver
{
    public const string Endpoint = "https://bsky.social/xrpc/com.atproto.identity.resolveHandle";

    public async Task<string> ResolveDidAsync(string handle, CancellationToken ct = default)
    {
        SocialResponse response;
        try
        {
            response = await SocialHttp.GetAsync(clients.CreateClient(SocialHttp.Bluesky),
                $"{Endpoint}?handle={Uri.EscapeDataString(handle)}", 64 * 1024, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or SocialResponseTooLargeException
            || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new UpstreamUnavailableException("Bluesky could not be asked to resolve the handle, so the link was not saved. Try again in a moment.", ex);
        }

        if (response.Status == HttpStatusCode.BadRequest)
            throw new ValidationException($"{SocialLinkParser.BlueskyField}: the handle '{handle}' does not resolve to an account.");

        if (!response.IsSuccess)
            throw new UpstreamUnavailableException($"Bluesky answered {(int)response.Status} when asked to resolve the handle, so the link was not saved. Try again in a moment.");

        try
        {
            using var document = JsonDocument.Parse(response.Body);
            var did = document.RootElement.GetProperty("did").GetString();
            if (did is not null && did.StartsWith("did:", StringComparison.Ordinal))
                return did;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new UpstreamUnavailableException("Bluesky answered something unexpected when asked to resolve the handle, so the link was not saved.", ex);
        }

        throw new UpstreamUnavailableException("Bluesky answered something unexpected when asked to resolve the handle, so the link was not saved.");
    }
}
