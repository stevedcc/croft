using System.Net;
using System.Security.Claims;
using System.Text;
using Croft.Web.Options;
using Croft.Web.Services;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Croft.Web.Tests;

public class UserAccessTokenServiceTests
{
    private const string TokenEndpoint = "https://idp.example/token";
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetAccessTokenAsync_NoBlogSidClaim_ReturnsNull()
    {
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", new TokenEntry("at-1", "rt-1", Now.AddSeconds(3600)));
        var handler = new FakeTokenEndpointHandler();
        var service = NewService(store, handler);

        var token = await service.GetAccessTokenAsync(UserWithSid(null));

        token.Should().BeNull();
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task GetAccessTokenAsync_NoStoreEntry_ReturnsNull()
    {
        var handler = new FakeTokenEndpointHandler();
        var service = NewService(new InMemoryTokenStore(), handler);

        var token = await service.GetAccessTokenAsync(UserWithSid("s1"));

        token.Should().BeNull();
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task GetAccessTokenAsync_FreshToken_ReturnsItWithoutHttpRequest()
    {
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", new TokenEntry("at-1", "rt-1", Now.AddSeconds(3600)));
        var handler = new FakeTokenEndpointHandler();
        var service = NewService(store, handler);

        var token = await service.GetAccessTokenAsync(UserWithSid("s1"));

        token.Should().Be("at-1");
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task GetAccessTokenAsync_NearExpiry_RefreshesAndStoresRotatedTokens()
    {
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", new TokenEntry("at-old", "rt-old", Now.AddSeconds(30)));
        var handler = new FakeTokenEndpointHandler
        {
            ResponseBody = """{"access_token":"at-new","refresh_token":"rt-new","expires_in":3600}"""
        };
        var service = NewService(store, handler);

        var token = await service.GetAccessTokenAsync(UserWithSid("s1"));

        token.Should().Be("at-new");
        handler.RequestCount.Should().Be(1);
        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri.Should().Be(new Uri(TokenEndpoint));
        handler.LastRequestBody.Should().Be(
            "grant_type=refresh_token&refresh_token=rt-old&client_id=blog-web&client_secret=secret");
        (await store.GetAsync("s1")).Should().Be(new TokenEntry("at-new", "rt-new", Now.AddSeconds(3600), SessionExpiresAt: Now.AddDays(30)));
    }

    [Fact]
    public async Task GetAccessTokenAsync_RefreshExpiresInInTheResponse_MovesTheSessionExpiryForward()
    {
        // The store shares the test clock: it treats a session past its expiry as missing.
        var store = new InMemoryTokenStore(new FakeTimeProvider(Now));
        await store.SetAsync("s1", new TokenEntry("at-old", "rt-old", Now.AddSeconds(30), SessionExpiresAt: Now.AddDays(1)));
        var handler = new FakeTokenEndpointHandler
        {
            ResponseBody = """{"access_token":"at-new","expires_in":300,"refresh_expires_in":604800}"""
        };
        var service = NewService(store, handler);

        await service.GetAccessTokenAsync(UserWithSid("s1"));

        (await store.GetAsync("s1"))!.SessionExpiresAt.Should().Be(Now.AddDays(7));
    }

    [Fact]
    public async Task GetAccessTokenAsync_RefreshWithoutNewRefreshToken_KeepsOldOne()
    {
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", new TokenEntry("at-old", "rt-old", Now.AddSeconds(30)));
        var handler = new FakeTokenEndpointHandler
        {
            ResponseBody = """{"access_token":"at-new","expires_in":3600}"""
        };
        var service = NewService(store, handler);

        var token = await service.GetAccessTokenAsync(UserWithSid("s1"));

        token.Should().Be("at-new");
        (await store.GetAsync("s1")).Should().Be(new TokenEntry("at-new", "rt-old", Now.AddSeconds(3600), SessionExpiresAt: Now.AddDays(30)));
    }

    [Fact]
    public async Task GetAccessTokenAsync_Refresh_KeepsTheIdTokenFromLogin()
    {
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", new TokenEntry("at-old", "rt-old", Now.AddSeconds(30), "idt-1"));
        var handler = new FakeTokenEndpointHandler
        {
            ResponseBody = """{"access_token":"at-new","refresh_token":"rt-new","expires_in":3600}"""
        };
        var service = NewService(store, handler);

        var token = await service.GetAccessTokenAsync(UserWithSid("s1"));

        token.Should().Be("at-new");
        (await store.GetAsync("s1"))!.IdToken.Should().Be("idt-1",
            "a refresh response carries no new ID token; the one from login must survive for id_token_hint");
    }

    [Fact]
    public async Task GetAccessTokenAsync_TenConcurrentCallsNearExpiry_TriggerExactlyOneRefresh()
    {
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", new TokenEntry("at-old", "rt-old", Now.AddSeconds(30)));
        var handler = new FakeTokenEndpointHandler
        {
            ResponseBody = """{"access_token":"at-new","refresh_token":"rt-new","expires_in":3600}"""
        };
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Gate = () => gate.Task;
        var service = NewService(store, handler);
        var user = UserWithSid("s1");

        var calls = Enumerable.Range(0, 10).Select(_ => service.GetAccessTokenAsync(user)).ToArray();
        gate.SetResult(true);
        var tokens = await Task.WhenAll(calls);

        handler.RequestCount.Should().Be(1, "concurrent callers must share one refresh");
        tokens.Should().AllBe("at-new");
    }

    [Fact]
    public async Task GetAccessTokenAsync_FirstCallerCancelsMidRefresh_SharedRefreshContinuesForOtherCallers()
    {
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", new TokenEntry("at-old", "rt-old", Now.AddSeconds(30)));
        var handler = new FakeTokenEndpointHandler
        {
            ResponseBody = """{"access_token":"at-new","refresh_token":"rt-new","expires_in":3600}"""
        };
        var requestArrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Gate = () =>
        {
            requestArrived.TrySetResult(true);
            return releaseResponse.Task;
        };
        var service = NewService(store, handler);
        var user = UserWithSid("s1");
        using var caller1Cts = new CancellationTokenSource();

        var caller1 = service.GetAccessTokenAsync(user, caller1Cts.Token);
        var caller2 = service.GetAccessTokenAsync(user);

        await requestArrived.Task;
        caller1Cts.Cancel();
        releaseResponse.SetResult(true);

        Func<Task> caller1Leaves = () => caller1;
        await caller1Leaves.Should().ThrowAsync<OperationCanceledException>(
            "a caller may stop waiting on its own token");
        (await caller2).Should().Be("at-new", "a second caller must still get the shared refresh's token");
        handler.RequestCount.Should().Be(1,
            "a caller disconnecting must not abort the shared refresh and waste the rotated refresh token");
        (await store.GetAsync("s1")).Should().Be(new TokenEntry("at-new", "rt-new", Now.AddSeconds(3600), SessionExpiresAt: Now.AddDays(30)),
            "the shared refresh must run to completion and store its result");
    }

    [Fact]
    public async Task GetAccessTokenAsync_Refresh_DisposesTheHttpResponseMessage()
    {
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", new TokenEntry("at-old", "rt-old", Now.AddSeconds(30)));
        var handler = new FakeTokenEndpointHandler
        {
            ResponseBody = """{"access_token":"at-new","refresh_token":"rt-new","expires_in":3600}"""
        };
        var service = NewService(store, handler);

        await service.GetAccessTokenAsync(UserWithSid("s1"));

        handler.LastResponse.Should().BeOfType<TrackingHttpResponseMessage>()
            .Which.Disposed.Should().BeTrue("the refresh's HttpResponseMessage must be disposed");
    }

    [Fact]
    public async Task GetAccessTokenAsync_InvalidGrant_ReturnsNullAndRemovesEntry()
    {
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", new TokenEntry("at-old", "rt-old", Now.AddSeconds(30)));
        var handler = new FakeTokenEndpointHandler
        {
            ResponseStatusCode = HttpStatusCode.BadRequest,
            ResponseBody = """{"error":"invalid_grant"}"""
        };
        var service = NewService(store, handler);

        var token = await service.GetAccessTokenAsync(UserWithSid("s1"));

        token.Should().BeNull();
        (await store.GetAsync("s1")).Should().BeNull("a rejected refresh token means the session is gone");
    }

    [Fact]
    public async Task GetAccessTokenAsync_NearExpiryWithoutRefreshToken_RemovesEntryAndReturnsNull()
    {
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", new TokenEntry("at-old", null, Now.AddSeconds(30)));
        var handler = new FakeTokenEndpointHandler();
        var service = NewService(store, handler);

        var token = await service.GetAccessTokenAsync(UserWithSid("s1"));

        token.Should().BeNull();
        handler.RequestCount.Should().Be(0);
        (await store.GetAsync("s1")).Should().BeNull();
    }

    [Fact]
    public async Task GetAccessTokenAsync_NonStringAccessToken_KeepsEntryAndReturnsStillValidToken()
    {
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", new TokenEntry("at-old", "rt-old", Now.AddSeconds(30)));
        var handler = new FakeTokenEndpointHandler
        {
            ResponseBody = """{"access_token":123,"expires_in":3600}"""
        };
        var service = NewService(store, handler);

        var token = await service.GetAccessTokenAsync(UserWithSid("s1"));

        token.Should().Be("at-old", "a 2xx response without a usable access_token is a transient failure, not a rejection");
        (await store.GetAsync("s1")).Should().Be(new TokenEntry("at-old", "rt-old", Now.AddSeconds(30)));
    }

    [Fact]
    public async Task GetAccessTokenAsync_FractionalExpiresIn_StoresTruncatedExpiry()
    {
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", new TokenEntry("at-old", "rt-old", Now.AddSeconds(30)));
        var handler = new FakeTokenEndpointHandler
        {
            ResponseBody = """{"access_token":"at-new","expires_in":3600.5}"""
        };
        var service = NewService(store, handler);

        var token = await service.GetAccessTokenAsync(UserWithSid("s1"));

        token.Should().Be("at-new");
        (await store.GetAsync("s1")).Should().Be(new TokenEntry("at-new", "rt-old", Now.AddSeconds(3600), SessionExpiresAt: Now.AddDays(30)));
    }

    [Fact]
    public async Task GetAccessTokenAsync_NonStringRefreshToken_KeepsOldRefreshToken()
    {
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", new TokenEntry("at-old", "rt-old", Now.AddSeconds(30)));
        var handler = new FakeTokenEndpointHandler
        {
            ResponseBody = """{"access_token":"at-new","refresh_token":123,"expires_in":3600}"""
        };
        var service = NewService(store, handler);

        var token = await service.GetAccessTokenAsync(UserWithSid("s1"));

        token.Should().Be("at-new");
        (await store.GetAsync("s1")).Should().Be(new TokenEntry("at-new", "rt-old", Now.AddSeconds(3600), SessionExpiresAt: Now.AddDays(30)));
    }

    [Fact]
    public async Task GetAccessTokenAsync_MissingTokenEndpoint_KeepsEntryAndReturnsStillValidToken()
    {
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", new TokenEntry("at-old", "rt-old", Now.AddSeconds(30)));
        var handler = new FakeTokenEndpointHandler();
        var service = NewService(store, handler, tokenEndpoint: null);

        var token = await service.GetAccessTokenAsync(UserWithSid("s1"));

        token.Should().Be("at-old", "an unusable discovery document is a transient failure, not a rejection");
        handler.RequestCount.Should().Be(0);
        (await store.GetAsync("s1")).Should().Be(new TokenEntry("at-old", "rt-old", Now.AddSeconds(30)));
    }

    [Fact]
    public async Task GetAccessTokenAsync_TokenEndpoint400_RemovesEntryAndReturnsNull()
    {
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", new TokenEntry("at-old", "rt-old", Now.AddSeconds(30)));
        var handler = new FakeTokenEndpointHandler
        {
            ResponseStatusCode = HttpStatusCode.BadRequest,
            ResponseBody = """{"error":"invalid_grant"}"""
        };
        var service = NewService(store, handler);

        var token = await service.GetAccessTokenAsync(UserWithSid("s1"));

        token.Should().BeNull();
        (await store.GetAsync("s1")).Should().BeNull("a 400 from the token endpoint means the refresh token is dead");
    }

    [Fact]
    public async Task GetAccessTokenAsync_TokenEndpoint401_RemovesEntryAndReturnsNull()
    {
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", new TokenEntry("at-old", "rt-old", Now.AddSeconds(30)));
        var handler = new FakeTokenEndpointHandler
        {
            ResponseStatusCode = HttpStatusCode.Unauthorized,
            ResponseBody = """{"error":"invalid_client"}"""
        };
        var service = NewService(store, handler);

        var token = await service.GetAccessTokenAsync(UserWithSid("s1"));

        token.Should().BeNull();
        (await store.GetAsync("s1")).Should().BeNull("a 401 from the token endpoint means the refresh token is dead");
    }

    [Fact]
    public async Task GetAccessTokenAsync_TokenEndpoint503_KeepsEntryAndReturnsStillValidToken()
    {
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", new TokenEntry("at-old", "rt-old", Now.AddSeconds(30)));
        var handler = new FakeTokenEndpointHandler { ResponseStatusCode = HttpStatusCode.ServiceUnavailable };
        var service = NewService(store, handler);

        var token = await service.GetAccessTokenAsync(UserWithSid("s1"));

        token.Should().Be("at-old", "the stored token has not actually expired yet");
        (await store.GetAsync("s1")).Should().Be(new TokenEntry("at-old", "rt-old", Now.AddSeconds(30)),
            "a transient IdP failure must not end the session");
    }

    [Fact]
    public async Task GetAccessTokenAsync_TokenEndpoint503WithExpiredToken_KeepsEntryAndLaterRefreshSucceeds()
    {
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", new TokenEntry("at-old", "rt-old", Now.AddSeconds(-30)));
        var handler = new FakeTokenEndpointHandler { ResponseStatusCode = HttpStatusCode.ServiceUnavailable };
        var service = NewService(store, handler);
        var user = UserWithSid("s1");

        var token = await service.GetAccessTokenAsync(user);

        token.Should().BeNull("the stored token has actually expired");
        (await store.GetAsync("s1")).Should().Be(new TokenEntry("at-old", "rt-old", Now.AddSeconds(-30)),
            "a transient IdP failure must not end the session");

        handler.ResponseStatusCode = HttpStatusCode.OK;
        handler.ResponseBody = """{"access_token":"at-new","refresh_token":"rt-new","expires_in":3600}""";

        var refreshed = await service.GetAccessTokenAsync(user);

        refreshed.Should().Be("at-new", "the session must recover once the IdP is back");
        (await store.GetAsync("s1")).Should().Be(new TokenEntry("at-new", "rt-new", Now.AddSeconds(3600), SessionExpiresAt: Now.AddDays(30)));
    }

    [Fact]
    public async Task GetAccessTokenAsync_NetworkError_KeepsEntryAndReturnsStillValidToken()
    {
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", new TokenEntry("at-old", "rt-old", Now.AddSeconds(30)));
        var handler = new FakeTokenEndpointHandler { SendException = new HttpRequestException("connection refused") };
        var service = NewService(store, handler);

        var token = await service.GetAccessTokenAsync(UserWithSid("s1"));

        token.Should().Be("at-old", "the stored token has not actually expired yet");
        (await store.GetAsync("s1")).Should().Be(new TokenEntry("at-old", "rt-old", Now.AddSeconds(30)),
            "a network blip must not end the session");
    }

    [Fact]
    public async Task GetAccessTokenAsync_Timeout_KeepsEntryAndReturnsStillValidToken()
    {
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", new TokenEntry("at-old", "rt-old", Now.AddSeconds(30)));
        var handler = new FakeTokenEndpointHandler { SendException = new TaskCanceledException("timed out") };
        var service = NewService(store, handler);

        var token = await service.GetAccessTokenAsync(UserWithSid("s1"));

        token.Should().Be("at-old", "the stored token has not actually expired yet");
        (await store.GetAsync("s1")).Should().Be(new TokenEntry("at-old", "rt-old", Now.AddSeconds(30)),
            "a timed-out refresh must not end the session");
    }

    [Fact]
    public async Task GetAccessTokenAsync_DiscoveryFailure_KeepsEntryAndReturnsStillValidToken()
    {
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", new TokenEntry("at-old", "rt-old", Now.AddSeconds(30)));
        var handler = new FakeTokenEndpointHandler();
        var options = DefaultOptions(TokenEndpoint);
        options.ConfigurationManager = new FakeConfigurationManager(TokenEndpoint)
        {
            Failure = new HttpRequestException("discovery is down")
        };
        var service = NewService(store, handler, options);

        var token = await service.GetAccessTokenAsync(UserWithSid("s1"));

        token.Should().Be("at-old", "the stored token has not actually expired yet");
        handler.RequestCount.Should().Be(0);
        (await store.GetAsync("s1")).Should().Be(new TokenEntry("at-old", "rt-old", Now.AddSeconds(30)),
            "a discovery outage must not end the session");
    }

    [Fact]
    public async Task GetAccessTokenAsync_MissingConfigurationManager_KeepsEntryAndReturnsStillValidToken()
    {
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", new TokenEntry("at-old", "rt-old", Now.AddSeconds(30)));
        var handler = new FakeTokenEndpointHandler();
        var options = DefaultOptions(TokenEndpoint);
        options.ConfigurationManager = null;
        var service = NewService(store, handler, options);

        var token = await service.GetAccessTokenAsync(UserWithSid("s1"));

        token.Should().Be("at-old", "the stored token has not actually expired yet");
        handler.RequestCount.Should().Be(0);
        (await store.GetAsync("s1")).Should().Be(new TokenEntry("at-old", "rt-old", Now.AddSeconds(30)));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"access_token":"","expires_in":3600}""")]
    public async Task GetAccessTokenAsync_ResponseWithoutUsableAccessToken_KeepsEntryAndReturnsStillValidToken(string responseBody)
    {
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", new TokenEntry("at-old", "rt-old", Now.AddSeconds(30)));
        var handler = new FakeTokenEndpointHandler { ResponseBody = responseBody };
        var service = NewService(store, handler);

        var token = await service.GetAccessTokenAsync(UserWithSid("s1"));

        token.Should().Be("at-old", "the stored token has not actually expired yet");
        (await store.GetAsync("s1")).Should().Be(new TokenEntry("at-old", "rt-old", Now.AddSeconds(30)),
            "a 2xx body without a usable access_token is a transient failure, not a rejection");
    }

    [Theory]
    [InlineData("""{"access_token":"at-new"}""")]
    [InlineData("""{"access_token":"at-new","expires_in":0}""")]
    [InlineData("""{"access_token":"at-new","expires_in":-5}""")]
    [InlineData("""{"access_token":"at-new","expires_in":"soon"}""")]
    public async Task GetAccessTokenAsync_ResponseWithoutUsableExpiresIn_StoresRefreshedTokensWithFallbackExpiry(string responseBody)
    {
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", new TokenEntry("at-old", "rt-old", Now.AddSeconds(30)));
        var handler = new FakeTokenEndpointHandler { ResponseBody = responseBody };
        var service = NewService(store, handler);

        var token = await service.GetAccessTokenAsync(UserWithSid("s1"));

        token.Should().Be("at-new", "the refresh succeeded; a missing expires_in must not make it a failure");
        (await store.GetAsync("s1")).Should().Be(new TokenEntry("at-new", "rt-old", Now.AddSeconds(300), SessionExpiresAt: Now.AddDays(30)),
            "without a usable expires_in and an opaque access token, default to 300 seconds");
    }

    [Fact]
    public async Task GetAccessTokenAsync_MissingExpiresInWithJwtAccessToken_UsesJwtExpClaim()
    {
        var exp = Now.AddSeconds(7200);
        var jwt = TestJwt.Create(exp);
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", new TokenEntry("at-old", "rt-old", Now.AddSeconds(30)));
        var handler = new FakeTokenEndpointHandler
        {
            ResponseBody = $$"""{"access_token":"{{jwt}}"}"""
        };
        var service = NewService(store, handler);

        var token = await service.GetAccessTokenAsync(UserWithSid("s1"));

        token.Should().Be(jwt);
        (await store.GetAsync("s1")).Should().Be(new TokenEntry(jwt, "rt-old", exp, SessionExpiresAt: Now.AddDays(30)),
            "without expires_in the access token's own exp claim says when it expires");
    }

    [Fact]
    public async Task GetAccessTokenAsync_TransientFailure_LogsWarningWithoutTokenValues()
    {
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", new TokenEntry("at-old", "rt-old", Now.AddSeconds(30)));
        var handler = new FakeTokenEndpointHandler { ResponseStatusCode = HttpStatusCode.ServiceUnavailable };
        var logger = new RecordingLogger<UserAccessTokenService>();
        var service = NewService(store, handler, logger: logger);

        await service.GetAccessTokenAsync(UserWithSid("s1"));

        logger.Warnings.Should().Contain(message => message.Contains("503"),
            "the warning must say what went wrong");
        logger.Warnings.Should().NotContain(message => message.Contains("at-old") || message.Contains("rt-old"),
            "token values must never be logged");
    }

    [Fact]
    public async Task AfterARefresh_AndTheSessionsLogout_NoRefreshLockRemainsForIt()
    {
        // M-15: the per-session lock map must not keep a session that has ended.
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", new TokenEntry("at-old", "rt-old", Now.AddSeconds(30)));
        var handler = new FakeTokenEndpointHandler
        {
            ResponseBody = """{"access_token":"at-new","refresh_token":"rt-new","expires_in":3600}"""
        };
        var service = NewService(store, handler);
        var user = UserWithSid("s1");

        (await service.GetAccessTokenAsync(user)).Should().Be("at-new", "the session really refreshed");
        await Croft.Web.Authentication.TokenSessionEvents.RemoveSessionAsync(user, store);

        service.RefreshLockCount.Should().Be(0);
    }

    [Fact]
    public async Task WhileARefreshIsInFlight_TheSessionHasOneLock_SharedByTheCallers_AndItIsGoneWhenTheRefreshEnds()
    {
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", new TokenEntry("at-old", "rt-old", Now.AddSeconds(30)));
        var handler = new FakeTokenEndpointHandler
        {
            ResponseBody = """{"access_token":"at-new","refresh_token":"rt-new","expires_in":3600}"""
        };
        var requestArrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Gate = () =>
        {
            requestArrived.TrySetResult(true);
            return release.Task;
        };
        var service = NewService(store, handler);
        var user = UserWithSid("s1");

        var first = service.GetAccessTokenAsync(user);
        await requestArrived.Task;
        var second = service.GetAccessTokenAsync(user);

        service.RefreshLockCount.Should().Be(1, "both callers share the session's one lock while the refresh runs");
        release.SetResult(true);
        await Task.WhenAll(first, second);

        handler.RequestCount.Should().Be(1);
        service.RefreshLockCount.Should().Be(0, "the lock is dropped as soon as nobody uses it");
    }

    [Fact]
    public async Task ARefreshThatFailsAfterItsCallerGaveUp_LogsOneWarning_NamingTheSessionButNoTokens()
    {
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", new TokenEntry("at-old", "rt-old", Now.AddSeconds(30)));
        var sessionLock = new FailingSessionLock();
        var logger = new RecordingLogger<UserAccessTokenService>();
        var service = NewService(store, sessionLock, new FakeTokenEndpointHandler(), logger: logger);
        using var callerCts = new CancellationTokenSource();

        var caller = service.GetAccessTokenAsync(UserWithSid("s1"), callerCts.Token);
        await sessionLock.Entered.Task;
        await callerCts.CancelAsync();
        Func<Task> callerLeaves = () => caller;
        await callerLeaves.Should().ThrowAsync<OperationCanceledException>();
        sessionLock.Fail.SetResult(true);

        await logger.WarningLogged.Task.WaitAsync(TimeSpan.FromSeconds(10));
        logger.Warnings.Should().ContainSingle()
            .Which.Should().Contain("s1").And.NotContain("at-old").And.NotContain("rt-old");
        logger.WarningExceptions.Should().ContainSingle().Which!.Message.Should().Be("the store is down");
        service.RefreshLockCount.Should().Be(0, "a failed refresh also drops its lock");
    }

    [Fact]
    public async Task ARefreshThatFaultsAfterTheCancellationFiredButBeforeTheCallerObservedIt_StillLogsOneWarning()
    {
        // Brief 14b item 4 (13i review Minor 1). In a circuit the caller's continuation runs on the
        // circuit's synchronization context, a little after the cancellation that completed
        // WaitAsync. The refresh can fault in that gap: the caller then sees OperationCanceledException
        // (its own token), the task is already complete, and `!refreshTask.IsCompleted` skipped the
        // logging, so the failure was never reported. A queueing context stands in for the circuit's.
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", new TokenEntry("at-old", "rt-old", Now.AddSeconds(30)));
        var sessionLock = new FailingSessionLock();
        var logger = new RecordingLogger<UserAccessTokenService>();
        var service = NewService(store, sessionLock, new FakeTokenEndpointHandler(), logger: logger);
        using var callerCts = new CancellationTokenSource();
        var circuit = new QueueingSynchronizationContext();
        var original = SynchronizationContext.Current;
        Task<string?> caller;
        try
        {
            SynchronizationContext.SetSynchronizationContext(circuit);
            caller = service.GetAccessTokenAsync(UserWithSid("s1"), callerCts.Token);
            sessionLock.Entered.Task.IsCompleted.Should().BeTrue("the refresh is running, blocked in the store's lock");

            // Completing a task from the circuit's own context would run the continuation inline: the
            // cancellation and the failure come from outside it, as they do in production.
            SynchronizationContext.SetSynchronizationContext(null);
            callerCts.Cancel();
            circuit.Pending.Should().Be(1, "the caller's continuation waits for the circuit");
            sessionLock.Fail.SetResult(true);
            SynchronizationContext.SetSynchronizationContext(circuit);

            circuit.RunEverythingButTheFirst();
            service.RefreshLockCount.Should().Be(0, "the refresh has run to its end: it faulted");
            circuit.RunTheFirst();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(original);
        }

        Func<Task> callerLeaves = () => caller;
        await callerLeaves.Should().ThrowAsync<OperationCanceledException>("the caller's own token fired");
        await logger.WarningLogged.Task.WaitAsync(TimeSpan.FromSeconds(10));
        logger.Warnings.Should().ContainSingle()
            .Which.Should().Contain("s1").And.NotContain("at-old").And.NotContain("rt-old");
        logger.WarningExceptions.Should().ContainSingle().Which!.Message.Should().Be("the store is down");
    }

    /// <summary>
    /// A synchronization context that only queues what is posted to it, so a test decides when each
    /// continuation runs. Everything happens on the test's own thread (the test never awaits while
    /// the context is installed), so the list needs no lock.
    /// </summary>
    private sealed class QueueingSynchronizationContext : SynchronizationContext
    {
        private readonly List<(SendOrPostCallback Callback, object? State)> _queue = [];

        public int Pending => _queue.Count;

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public void RunEverythingButTheFirst()
        {
            while (_queue.Count > 1)
            {
                var (callback, state) = _queue[1];
                _queue.RemoveAt(1);
                callback(state);
            }
        }

        public void RunTheFirst()
        {
            var (callback, state) = _queue[0];
            _queue.RemoveAt(0);
            callback(state);
        }
    }

    /// <summary>A session lock whose work fails (as a store outage would) once released, after the caller has left.</summary>
    private sealed class FailingSessionLock : ISessionLock
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Fail { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<T> RunLockedAsync<T>(
            string sessionId, Func<LockedSession, CancellationToken, Task<T>> work, CancellationToken ct = default)
        {
            Entered.SetResult(true);
            await Fail.Task;
            throw new InvalidOperationException("the store is down");
        }
    }

    private static UserAccessTokenService NewService(
        InMemoryTokenStore store,
        ISessionLock sessionLock,
        FakeTokenEndpointHandler handler,
        ILogger<UserAccessTokenService>? logger = null)
    {
        return new UserAccessTokenService(
            store,
            sessionLock,
            new FakeHttpClientFactory(handler),
            new FakeOidcOptionsMonitor(DefaultOptions(TokenEndpoint)),
            new FakeTimeProvider(Now),
            new AuthSettings(TimeSpan.FromDays(30)),
            logger ?? NullLogger<UserAccessTokenService>.Instance);
    }

    private static UserAccessTokenService NewService(
        InMemoryTokenStore store,
        FakeTokenEndpointHandler handler,
        string? tokenEndpoint = TokenEndpoint,
        ILogger<UserAccessTokenService>? logger = null)
        => NewService(store, handler, DefaultOptions(tokenEndpoint), logger);

    private static UserAccessTokenService NewService(
        InMemoryTokenStore store,
        FakeTokenEndpointHandler handler,
        OpenIdConnectOptions options,
        ILogger<UserAccessTokenService>? logger = null)
    {
        return new UserAccessTokenService(
            store,
            store,
            new FakeHttpClientFactory(handler),
            new FakeOidcOptionsMonitor(options),
            new FakeTimeProvider(Now),
            new AuthSettings(TimeSpan.FromDays(30)),
            logger ?? NullLogger<UserAccessTokenService>.Instance);
    }

    private static OpenIdConnectOptions DefaultOptions(string? tokenEndpoint) => new()
    {
        ClientId = "blog-web",
        ClientSecret = "secret",
        ConfigurationManager = new FakeConfigurationManager(tokenEndpoint)
    };

    private static ClaimsPrincipal UserWithSid(string? sid)
    {
        var claims = new List<Claim> { new("sub", "admin-id") };
        if (sid != null)
        {
            claims.Add(new Claim("blog_sid", sid));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private sealed class FakeConfigurationManager(string? tokenEndpoint) : IConfigurationManager<OpenIdConnectConfiguration>
    {
        public Exception? Failure;

        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel) =>
            Failure is null
                ? Task.FromResult(new OpenIdConnectConfiguration { TokenEndpoint = tokenEndpoint })
                : Task.FromException<OpenIdConnectConfiguration>(Failure);

        public void RequestRefresh() { }
    }

    private sealed class FakeOidcOptionsMonitor(OpenIdConnectOptions options) : IOptionsMonitor<OpenIdConnectOptions>
    {
        public OpenIdConnectOptions CurrentValue => options;

        public OpenIdConnectOptions Get(string? name) => options;

        public IDisposable? OnChange(Action<OpenIdConnectOptions, string?> listener) => null;
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private sealed class FakeTokenEndpointHandler : HttpMessageHandler
    {
        public int RequestCount;
        public HttpRequestMessage? LastRequest;
        public string? LastRequestBody;
        public TrackingHttpResponseMessage? LastResponse;
        public HttpStatusCode ResponseStatusCode = HttpStatusCode.OK;
        public string ResponseBody = "{}";
        public Func<Task>? Gate;
        public Exception? SendException;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref RequestCount);
            LastRequest = request;
            if (request.Content != null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            if (Gate != null)
            {
                // Like a real HTTP send: cancelling the request's token aborts the in-flight call.
                await Gate().WaitAsync(cancellationToken);
            }
            if (SendException != null)
            {
                throw SendException;
            }
            LastResponse = new TrackingHttpResponseMessage(ResponseStatusCode)
            {
                Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json")
            };
            return LastResponse;
        }
    }

    private sealed class TrackingHttpResponseMessage(HttpStatusCode statusCode) : HttpResponseMessage(statusCode)
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];

        public List<Exception?> WarningExceptions { get; } = [];

        /// <summary>Completed by the first Warning, so a test can wait for it instead of sleeping.</summary>
        public TaskCompletionSource WarningLogged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
                WarningExceptions.Add(exception);
                WarningLogged.TrySetResult();
            }
        }
    }
}
