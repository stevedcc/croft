namespace Croft.Api.Social;

/// <summary>Resolves a Bluesky handle to its DID (<c>com.atproto.identity.resolveHandle</c>), at save time only.</summary>
public interface IBlueskyHandleResolver
{
    /// <summary>
    /// The DID the handle resolves to. A handle that does not resolve throws <see cref="Errors.ValidationException"/>;
    /// a service that cannot answer (timeout, 5xx) throws <see cref="Errors.UpstreamUnavailableException"/>.
    /// </summary>
    Task<string> ResolveDidAsync(string handle, CancellationToken ct = default);
}
