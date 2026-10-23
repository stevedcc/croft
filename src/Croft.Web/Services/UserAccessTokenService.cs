using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Croft.Web.Authentication;
using Croft.Web.Options;
using Croft.Web.Telemetry;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Croft.Web.Services;

public sealed partial class UserAccessTokenService(
    ITokenStore store,
    ISessionLock sessionLock,
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<OpenIdConnectOptions> oidcOptions,
    TimeProvider timeProvider,
    AuthSettings authSettings,
    ILogger<UserAccessTokenService> logger) : IUserAccessTokenService
{
    private static readonly TimeSpan RefreshWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(10);

    // One gate per session with a refresh in flight on this instance, dropped (and disposed) when the
    // last user of it is done, so the map holds only running refreshes however a session ends: sign-out,
    // the purge, the session's expiry or a back-channel logout all leave nothing behind.
    // Guarded by a plain lock: the guarded work is two dictionary operations and a counter, never awaits,
    // so an async lock would only add a hop to every refresh.
    private readonly Dictionary<string, RefreshGate> _refreshGates = [];

    /// <summary>How many sessions have a refresh lock right now (for tests).</summary>
    internal int RefreshLockCount
    {
        get
        {
            lock (_refreshGates)
            {
                return _refreshGates.Count;
            }
        }
    }

    private sealed class RefreshGate
    {
        public SemaphoreSlim Lock { get; } = new(1, 1);

        /// <summary>Callers and the running refresh that hold the gate; guarded by the map's lock.</summary>
        public int Users { get; set; }
    }

    private RefreshGate AcquireGate(string sessionId)
    {
        lock (_refreshGates)
        {
            if (!_refreshGates.TryGetValue(sessionId, out var gate))
            {
                _refreshGates[sessionId] = gate = new RefreshGate();
            }

            gate.Users++;
            return gate;
        }
    }

    private void ReleaseGate(string sessionId, RefreshGate gate)
    {
        lock (_refreshGates)
        {
            if (--gate.Users > 0)
            {
                return;
            }

            _refreshGates.Remove(sessionId);
        }

        gate.Lock.Dispose();
    }

    public async Task<string?> GetAccessTokenAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        var sessionId = user.FindFirst(TokenSessionEvents.SessionIdClaim)?.Value;
        if (string.IsNullOrEmpty(sessionId))
        {
            return null;
        }

        var entry = await store.GetAsync(sessionId, ct);
        if (entry is null)
        {
            return null;
        }

        if (IsUsable(entry))
        {
            return entry.AccessToken;
        }

        // Concurrent callers for the same session share one refresh. The refresh runs as its own
        // task so that a caller that goes away only stops waiting: the shared refresh itself is not
        // aborted (its refresh token may already be rotated at the IdP), it stores its result, and
        // it releases the per-session lock exactly once.
        var gate = AcquireGate(sessionId);
        var refreshTask = RefreshUnderLockAsync(sessionId, gate);
        try
        {
            return await refreshTask.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // This caller leaves; the refresh runs on with nobody awaiting it, so a failure of it
            // would surface only as an unobserved task exception. Log it instead. The refresh may
            // already be over (it can fault between the cancellation that completed WaitAsync and
            // this handler, which a circuit's synchronization context makes a real gap): a
            // continuation on a completed task runs at once, so that failure is logged too.
            LogAbandonedRefreshOutcome(sessionId, refreshTask);
            throw;
        }
    }

    private void LogAbandonedRefreshOutcome(string sessionId, Task refreshTask) =>
        _ = refreshTask.ContinueWith(
            task => LogAbandonedRefreshFailed(logger, sessionId, task.Exception!.GetBaseException()),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>Releases the gate the caller acquired once the refresh is over, however it ends.</summary>
    private async Task<string?> RefreshUnderLockAsync(string sessionId, RefreshGate gate)
    {
        try
        {
            return await RefreshUnderGateAsync(sessionId, gate.Lock);
        }
        finally
        {
            ReleaseGate(sessionId, gate);
        }
    }

    private async Task<string?> RefreshUnderGateAsync(string sessionId, SemaphoreSlim refreshLock)
    {
        await refreshLock.WaitAsync();
        try
        {
            // Beyond this process's gate lies the shared store's session lock: the entry is
            // re-read under a row lock (a racing instance's refresh is already stored there),
            // and the refresh runs only if it is still needed. The loser of the race therefore
            // takes the winner's new token instead of spending the rotated refresh token twice
            // and having it revoked at the IdP.
            return await sessionLock.RunLockedAsync(
                sessionId, (locked, _) => RefreshUnderSessionLockAsync(sessionId, locked));
        }
        finally
        {
            refreshLock.Release();
        }
    }

    private async Task<string?> RefreshUnderSessionLockAsync(string sessionId, LockedSession locked)
    {
        var entry = locked.Entry;
        if (entry is null)
        {
            return null;
        }
        if (IsUsable(entry))
        {
            // Another caller (this instance or another) refreshed while we waited for the lock.
            return entry.AccessToken;
        }
        return await RefreshAsync(sessionId, locked, entry);
    }

    private bool IsUsable(TokenEntry entry) =>
        entry.ExpiresAt - timeProvider.GetUtcNow() > RefreshWindow;

    private async Task<string?> RefreshAsync(string sessionId, LockedSession locked, TokenEntry entry)
    {
        var refreshToken = entry.RefreshToken;
        if (string.IsNullOrEmpty(refreshToken))
        {
            // Expired and nothing to refresh with: the session is gone.
            WebMetrics.TokenRefreshFailed();
            SessionRemoval.LogNoRefreshToken(logger, sessionId);
            await locked.RemoveAsync();
            return null;
        }

        // The shared refresh runs under its own timeout and is never tied to a caller's token:
        // one caller disconnecting must not abort the refresh the other callers wait for
        // (the IdP may already have rotated the refresh token).
        using var refreshTimeout = new CancellationTokenSource(RefreshTimeout);

        var options = oidcOptions.Get(OpenIdConnectDefaults.AuthenticationScheme);
        var configurationManager = options.ConfigurationManager;
        if (configurationManager is null)
        {
            return TransientFailure(sessionId, entry, "the OIDC configuration manager is not available");
        }

        OpenIdConnectConfiguration configuration;
        try
        {
            configuration = await configurationManager.GetConfigurationAsync(refreshTimeout.Token);
        }
        catch (Exception ex)
        {
            return TransientFailure(sessionId, entry, "OIDC discovery failed", ex);
        }

        if (!Uri.TryCreate(configuration.TokenEndpoint, UriKind.Absolute, out var tokenEndpoint))
        {
            return TransientFailure(sessionId, entry, "the OIDC configuration has no usable token endpoint");
        }

        var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = options.ClientId ?? string.Empty,
                ["client_secret"] = options.ClientSecret ?? string.Empty
            })
        };

        HttpResponseMessage response;
        try
        {
            response = await httpClientFactory.CreateClient().SendAsync(request, refreshTimeout.Token);
        }
        catch (HttpRequestException ex)
        {
            return TransientFailure(sessionId, entry, "the token request failed with a network error", ex);
        }
        catch (OperationCanceledException ex)
        {
            return TransientFailure(sessionId, entry, "the token request timed out", ex);
        }

        using (response)
        {
            return await StoreRefreshedTokensAsync(sessionId, locked, entry, response, refreshTimeout.Token);
        }
    }

    private async Task<string?> StoreRefreshedTokensAsync(
        string sessionId,
        LockedSession locked,
        TokenEntry entry,
        HttpResponseMessage response,
        CancellationToken ct)
    {
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            // e.g. 400 invalid_grant or 401 invalid_client: the refresh token is rejected, so the session is gone.
            WebMetrics.TokenRefreshFailed();
            var (error, errorDescription) = await ReadOAuthErrorAsync(response, ct);
            SessionRemoval.LogRefreshRejected(logger, sessionId, (int)response.StatusCode, error, errorDescription);
            await locked.RemoveAsync(ct);
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            // 5xx, 429, ...: the IdP is having a bad moment, but the refresh token is still good.
            return TransientFailure(sessionId, entry, $"the token endpoint returned HTTP {(int)response.StatusCode}");
        }

        string newAccessToken;
        string? newRefreshToken;
        DateTimeOffset expiresAt;
        DateTimeOffset sessionExpiresAt;
        try
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = body.RootElement;
            var accessToken = root.TryGetProperty("access_token", out var at) && at.ValueKind == JsonValueKind.String
                ? at.GetString()
                : null;
            if (string.IsNullOrEmpty(accessToken))
            {
                return TransientFailure(sessionId, entry, "the 2xx response has no usable access_token");
            }
            newAccessToken = accessToken;
            // A missing or wrong-typed refresh token means "no rotation": keep the old one.
            newRefreshToken = root.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String
                ? rt.GetString()
                : null;
            // A missing or unusable expires_in falls back to the token's exp claim,
            // then to a 300-second default; it must not fail the refresh.
            var expiresInSeconds = root.TryGetProperty("expires_in", out var ei)
                ? ei.ValueKind switch
                {
                    JsonValueKind.String => ei.GetString(),
                    JsonValueKind.Number => ei.GetRawText(),
                    _ => null
                }
                : null;
            expiresAt = TokenExpiry.ResolveExpiresAt(timeProvider.GetUtcNow(), expiresInSeconds, newAccessToken);
            // Every successful refresh moves the session's end forward, as at sign-in.
            var refreshExpiresInSeconds = root.TryGetProperty("refresh_expires_in", out var re)
                ? re.ValueKind switch
                {
                    JsonValueKind.String => re.GetString(),
                    JsonValueKind.Number => re.GetRawText(),
                    _ => null
                }
                : null;
            sessionExpiresAt = TokenExpiry.ResolveSessionExpiresAt(
                timeProvider.GetUtcNow(), refreshExpiresInSeconds, authSettings.SessionLifetime);
        }
        catch (JsonException)
        {
            // The response body is never logged: it can contain token values.
            return TransientFailure(sessionId, entry, "the 2xx response is not valid JSON");
        }
        catch (OperationCanceledException ex)
        {
            return TransientFailure(sessionId, entry, "the token request timed out", ex);
        }

        // Refresh token rotation: replace it when the server sends a new one, keep it otherwise.
        // The ID token from login is carried over unchanged: a refresh response has none.
        var refreshed = entry with
        {
            AccessToken = newAccessToken,
            RefreshToken = string.IsNullOrEmpty(newRefreshToken) ? entry.RefreshToken : newRefreshToken,
            ExpiresAt = expiresAt,
            SessionExpiresAt = sessionExpiresAt
        };
        await locked.SetAsync(refreshed, ct);
        WebMetrics.TokenRefreshSucceeded();
        return refreshed.AccessToken;
    }

    /// <summary>
    /// The <c>error</c> and <c>error_description</c> of an OAuth error response (RFC 6749 5.2) and
    /// nothing else of the body, which can carry token values. A body that is not that JSON reads
    /// as no error text.
    /// </summary>
    private static async Task<(string? Error, string? Description)> ReadOAuthErrorAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return (ReadString(body.RootElement, "error"), ReadString(body.RootElement, "error_description"));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or OperationCanceledException)
        {
            return (null, null);
        }

        static string? ReadString(JsonElement root, string name) =>
            root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    /// <summary>
    /// A failure that says nothing about the refresh token itself (IdP outage, network error, ...):
    /// the entry stays exactly as it is, and the current token is used until it actually expires.
    /// </summary>
    private string? TransientFailure(string sessionId, TokenEntry entry, string reason, Exception? exception = null)
    {
        WebMetrics.TokenRefreshTransientFailure();
        LogTransientRefreshFailure(logger, sessionId, reason, exception);
        return entry.ExpiresAt > timeProvider.GetUtcNow() ? entry.AccessToken : null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Access token refresh for session {SessionId} failed after its caller gave up; nobody was waiting for the result.")]
    private static partial void LogAbandonedRefreshFailed(ILogger logger, string sessionId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Access token refresh for session {SessionId} failed transiently ({Reason}); keeping the entry.")]
    private static partial void LogTransientRefreshFailure(ILogger logger, string sessionId, string reason, Exception? exception);
}
