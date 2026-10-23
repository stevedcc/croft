using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using Croft.Shared.Telemetry;

namespace Croft.Api.Social;

/// <summary>The answer of an outbound social request: the status, the headers the schedule reads, and the body when it was a success.</summary>
public sealed record SocialResponse(HttpStatusCode Status, HttpResponseHeaders Headers, byte[] Body)
{
    public bool IsSuccess => (int)Status is >= 200 and < 300;
}

/// <summary>The response was larger than the cap for its kind: it is never read past the cap.</summary>
public sealed class SocialResponseTooLargeException(long limit)
    : Exception($"The response is larger than {limit} bytes.");

/// <summary>
/// The three named HttpClients that talk to Mastodon, Bluesky and the avatar hosts (brief 12a), and the one way to read their answers:
/// with a size cap (a body is never read with an unbounded <c>ReadAsStringAsync</c>) and a time limit over the headers and the body together.
/// </summary>
public static class SocialHttp
{
    public const string Mastodon = "SocialMastodon";
    public const string Bluesky = "SocialBluesky";
    public const string Avatars = "SocialAvatars";

    /// <summary>The client that posts announcements (brief 12d): never retries (a retry of a POST could post twice), 30 s per request (a blob upload takes time).</summary>
    public const string Announce = "SocialAnnounce";

    /// <summary>The most of an error answer that is kept (the part that carries a platform's error message).</summary>
    public const long ErrorBodyLimit = 8 * 1024;

    public static readonly TimeSpan AnnounceTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The cap for a thread (Mastodon context, Bluesky thread) and for a handle resolution answer.</summary>
    public const long ThreadLimit = 2 * 1024 * 1024;

    public const long AvatarLimit = 1024 * 1024;

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Registers the three clients: the SSRF-guarded primary handler (no redirects, no proxy, no cookies), the standard resilience handler with a
    /// 10 s limit and <b>no retries</b> (the schedule retries), a User-Agent naming the software and the site
    /// (<c>Croft/{version} (+{Blog:BaseUrl})</c>), and no request logging: an avatar's URL can carry a handle, and the logs carry none.
    /// </summary>
    public static IServiceCollection AddSocialClients(this IServiceCollection services, IConfiguration configuration)
    {
        var userAgent = UserAgent(configuration["Blog:BaseUrl"]);
        services.AddSingleton<SocialEgressGuard>();
        foreach (var name in new[] { Mastodon, Bluesky, Avatars, Announce })
        {
            var timeout = name == Announce ? AnnounceTimeout : RequestTimeout;
            var client = services.AddHttpClient(name, http =>
            {
                // The resilience handler owns the time budget (and GetAsync the body's); the client's own 100 s default must not interfere.
                http.Timeout = Timeout.InfiniteTimeSpan;
                http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
            });
            client.RemoveAllLoggers();
            client.ConfigurePrimaryHttpMessageHandler(provider => new SocketsHttpHandler
            {
                ConnectCallback = provider.GetRequiredService<SocialEgressGuard>().ConnectAsync,
                ConnectTimeout = timeout,
                AllowAutoRedirect = false,
                UseProxy = false, // a proxy would be connected to instead of the address the guard checked
                UseCookies = false,
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10), // so a changed DNS answer is picked up (and checked again)
            });
            client.AddStandardResilienceHandler(options =>
            {
                options.AttemptTimeout.Timeout = timeout;
                options.TotalRequestTimeout.Timeout = timeout;
                options.CircuitBreaker.SamplingDuration = timeout * 2; // the breaker's validation wants at least twice the attempt timeout

                // WORKAROUND(W-100, Polly): the retry strategy rejects MaxRetryAttempts = 0, so "no retries" is a strategy that never handles anything
                options.Retry.MaxRetryAttempts = 1;
                options.Retry.ShouldHandle = _ => ValueTask.FromResult(false);
                // END WORKAROUND(W-100)
            });
        }

        return services;
    }

    public static string UserAgent(string? baseUrl)
    {
        var version = typeof(SocialHttp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0";
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
            version = version[..plus];

        return string.IsNullOrWhiteSpace(baseUrl) ? $"Croft/{version}" : $"Croft/{version} (+{baseUrl.Trim()})";
    }

    /// <summary>
    /// GETs <paramref name="url"/> on <paramref name="client"/>. A success body is read up to <paramref name="maxBytes"/>
    /// (one byte more throws <see cref="SocialResponseTooLargeException"/>, a larger Content-Length throws at once); another status
    /// returns its headers and no body. The whole exchange is under <see cref="RequestTimeout"/>. With <paramref name="hostOnlyTelemetry"/>
    /// the request's span exports its host alone (<see cref="TelemetryRedaction"/>).
    /// </summary>
    public static async Task<SocialResponse> GetAsync(HttpClient client, string url, long maxBytes, CancellationToken ct, bool hostOnlyTelemetry = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (hostOnlyTelemetry)
            request.Options.Set(TelemetryRedaction.HostOnly, true);

        return await SendAsync(client, request, maxBytes, RequestTimeout, keepErrorBody: false, ct);
    }

    /// <summary>
    /// Sends <paramref name="request"/> (any method) with the same limits as <see cref="GetAsync"/>: a success body is read up to <paramref name="maxBytes"/>,
    /// the whole exchange is under <paramref name="timeout"/>. With <paramref name="keepErrorBody"/> an error answer keeps its first
    /// <see cref="ErrorBodyLimit"/> bytes (where a platform's error message is); otherwise it returns its headers and no body.
    /// </summary>
    public static async Task<SocialResponse> SendAsync(
        HttpClient client, HttpRequestMessage request, long maxBytes, TimeSpan timeout, bool keepErrorBody, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode)
        {
            if (!keepErrorBody)
                return new SocialResponse(response.StatusCode, response.Headers, []);

            await using var errorStream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var error = new MemoryStream();
            var chunk = new byte[1024];
            int got;
            while (error.Length < ErrorBodyLimit && (got = await errorStream.ReadAsync(chunk, deadline.Token)) > 0)
                error.Write(chunk, 0, (int)Math.Min(got, ErrorBodyLimit - error.Length));
            return new SocialResponse(response.StatusCode, response.Headers, error.ToArray());
        }

        if (response.Content.Headers.ContentLength > maxBytes)
            throw new SocialResponseTooLargeException(maxBytes);

        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var body = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, deadline.Token)) > 0)
        {
            if (body.Length + read > maxBytes)
                throw new SocialResponseTooLargeException(maxBytes);
            body.Write(buffer, 0, read);
        }

        return new SocialResponse(response.StatusCode, response.Headers, body.ToArray());
    }
}
