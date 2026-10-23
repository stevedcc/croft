using System.Net;
using Croft.Web.Options;
using Croft.Web.Services;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Croft.Web.Tests;

/// <summary>
/// Brief 13b item 5 (review finding 10): in the single-instance deployment (no Web database) a
/// session ends at its session expiry like it does with the Postgres store: the entry reads as
/// missing, so the cookie is rejected and the token service never refreshes it, and the store
/// does not collect dead sessions until the process restarts.
/// </summary>
public class InMemorySessionExpiryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ASessionOneSecondPastItsExpiry_IsMissing_ButOneUntilItsExpiryIsNot()
    {
        var store = new InMemoryTokenStore(new FakeTimeProvider(Now));
        await store.SetAsync("ended", Entry(sessionExpiresAt: Now.AddSeconds(-1)));
        await store.SetAsync("live", Entry(sessionExpiresAt: Now.AddSeconds(1)));
        await store.SetAsync("legacy", Entry(sessionExpiresAt: null));

        (await store.GetAsync("ended")).Should().BeNull("a session past its expiry is gone");
        (await store.GetAsync("live")).Should().NotBeNull();
        (await store.GetAsync("legacy")).Should().NotBeNull("an entry with no session expiry has no end to enforce");
        (await store.RunLockedAsync("ended", (session, _) => Task.FromResult(session.Entry))).Should().BeNull(
            "the refresh path must not see an ended session either");
    }

    [Fact]
    public async Task TheTokenService_NeverRefreshesAnEndedSession_AndNoRequestReachesTheIdP()
    {
        var store = new InMemoryTokenStore(new FakeTimeProvider(Now));
        // Access token long expired and a refresh token: without the session expiry this is refreshed.
        await store.SetAsync("ended", Entry(sessionExpiresAt: Now.AddSeconds(-1)));
        await store.SetAsync("live", Entry(sessionExpiresAt: Now.AddDays(1)));
        var idp = new SessionTestSupport.CannedTokenEndpoint(HttpStatusCode.OK,
            """{"access_token":"at-new","refresh_token":"rt-new","expires_in":300,"refresh_expires_in":2592000}""");
        var service = NewTokenService(store, idp);

        (await service.GetAccessTokenAsync(User("ended"))).Should().BeNull();
        idp.RequestCount.Should().Be(0, "an ended session must not cost the IdP a refresh request");

        (await service.GetAccessTokenAsync(User("live"))).Should().Be("at-new",
            "the control: the same entry with a live session is refreshed, so the test can tell");
        idp.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task TheNextRequest_WithTheCookieOfAnEndedSession_IsSignedOut()
    {
        var clock = new FakeTimeProvider(Now);
        using var factory = CreateFactory(clock);
        var store = factory.Services.GetRequiredService<ITokenStore>();
        await store.SetAsync("s-live", Entry(sessionExpiresAt: Now.AddSeconds(30)));
        var cookie = await AuthCookie.SignInAsync(factory.Services, "s-live", "Admin");

        (await AuthCookie.AuthenticateAsync(factory.Services, cookie)).Succeeded.Should().BeTrue("the control: the session is alive");

        clock.UtcNow = Now.AddSeconds(31);

        (await AuthCookie.AuthenticateAsync(factory.Services, cookie)).Succeeded.Should().BeFalse(
            "a session one second past its lifetime is signed out on the next request");
    }

    [Fact]
    public async Task ThePurge_RemovesEndedSessions_EveryInterval()
    {
        var clock = new ManualTimeProvider(Now);
        var store = new InMemoryTokenStore(clock);
        await store.SetAsync("soon", Entry(sessionExpiresAt: Now.AddSeconds(30)));
        await store.SetAsync("later", Entry(sessionExpiresAt: Now.AddHours(1)));
        using var purger = new InMemorySessionPurgeHostedService(store, NullLogger<InMemorySessionPurgeHostedService>.Instance, clock);

        await purger.StartAsync(CancellationToken.None);
        try
        {
            await clock.WaitForTimersAsync(1);
            store.Count.Should().Be(2, "nothing has ended at the first tick");

            await clock.AdvanceAsync(TimeSpan.FromMinutes(1), expectTimers: 2);

            store.Count.Should().Be(1, "the session that ended is freed by the next tick; the live one stays");
            (await store.GetAsync("later")).Should().NotBeNull();
        }
        finally
        {
            await purger.StopAsync(CancellationToken.None);
        }
    }

    private static TokenEntry Entry(DateTimeOffset? sessionExpiresAt) =>
        new("at-old", "rt-old", Now.AddMinutes(-10), "idt", "sid", "sub", sessionExpiresAt);

    private static System.Security.Claims.ClaimsPrincipal User(string sessionId) =>
        new(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim("sub", "sub"), new System.Security.Claims.Claim("blog_sid", sessionId)], "test"));

    private static UserAccessTokenService NewTokenService(InMemoryTokenStore store, SessionTestSupport.CannedTokenEndpoint idp) =>
        new(store, store,
            new SessionTestSupport.StaticHttpClientFactory(idp),
            new SessionTestSupport.StaticOptionsMonitor(SessionTestSupport.OidcOptions()),
            new FakeTimeProvider(Now),
            new AuthSettings(TimeSpan.FromDays(30)),
            NullLogger<UserAccessTokenService>.Instance);

    private static WebApplicationFactory<Program> CreateFactory(TimeProvider clock) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("Oidc:Authority", "http://idp.example.com");
            builder.UseSetting("Oidc:ClientId", "blog-web");
            builder.ConfigureTestServices(services => services.AddSingleton(clock));
        });
}
