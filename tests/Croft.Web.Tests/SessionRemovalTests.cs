using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using Croft.Web.Authentication;
using Croft.Web.Data;
using Croft.Web.Options;
using Croft.Web.Services;
using Croft.Web.Tests.Fixtures;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Croft.Web.Tests;

/// <summary>
/// Every way a session ends (a missing refresh token, a rejected refresh, a cookie whose entry is
/// gone, the purge, a back-channel logout) is logged once at Information with its reason, and
/// counted under the same reason, so the owner can tell from the logs why they were signed out.
/// Neither the logs nor the counter ever carry a token or a whole session id.
/// </summary>
[Collection(nameof(CroftMetricsGroup))]
[Trait("Category", "Container")]
public class SessionRemovalTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private const string FullSessionId = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task NoRefreshToken_IsLoggedOnceWithItsReasonAndCounted()
    {
        using var metrics = new MetricCapture("Croft.Web").Start();
        var store = new InMemoryTokenStore();
        await store.SetAsync(FullSessionId, new TokenEntry("at-old", null, Now.AddMinutes(-1)));
        var logger = new CapturingLogger<UserAccessTokenService>();

        var token = await NewTokenService(store, HttpStatusCode.OK, "{}", logger).GetAccessTokenAsync(UserWithSid(FullSessionId));

        token.Should().BeNull();
        var entry = logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information).Which;
        entry.Properties["Reason"].Should().Be("NoRefreshToken");
        entry.Properties["SessionId"].Should().Be("01234567", "only the first 8 hex characters of a session id are logged");
        logger.AllText.Should().NotContain(FullSessionId).And.NotContain("at-old");
        metrics.Measurements.Should().ContainSingle(m =>
            m.Instrument == "croft.web.session.removed" && m.Value == 1 && m.HasTag("reason", "NoRefreshToken"));
    }

    [Fact]
    public async Task RefreshRejected_IsLoggedOnceWithStatusErrorAndDescription_AndNoTokenValue()
    {
        using var metrics = new MetricCapture("Croft.Web").Start();
        var store = new InMemoryTokenStore();
        await store.SetAsync(FullSessionId, new TokenEntry("at-old", "rt-secret", Now.AddMinutes(-1)));
        var logger = new CapturingLogger<UserAccessTokenService>();
        const string body = """
            {"error":"invalid_grant","error_description":"Refresh token has expired","refresh_token":"rt-echoed","access_token":"at-echoed"}
            """;

        var token = await NewTokenService(store, HttpStatusCode.BadRequest, body, logger)
            .GetAccessTokenAsync(UserWithSid(FullSessionId));

        token.Should().BeNull();
        var entry = logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information).Which;
        entry.Properties["Reason"].Should().Be("RefreshRejected");
        entry.Properties["Status"].Should().Be(400);
        entry.Properties["Error"].Should().Be("invalid_grant");
        entry.Properties["ErrorDescription"].Should().Be("Refresh token has expired");
        entry.Properties["SessionId"].Should().Be("01234567");
        logger.AllText.Should().NotContain("rt-secret").And.NotContain("rt-echoed").And.NotContain("at-echoed")
            .And.NotContain("at-old").And.NotContain(FullSessionId);
        metrics.Measurements.Should().ContainSingle(m =>
            m.Instrument == "croft.web.session.removed" && m.Value == 1 && m.HasTag("reason", "RefreshRejected"));
        metrics.Measurements.Should().Contain(m => m.Instrument == "croft.token.refreshes" && m.HasTag("outcome", "failed"),
            "the existing TokenRefreshFailed counter stays as it was");
    }

    [Fact]
    public async Task EntryMissing_IsLoggedOnceWithItsReasonAndCounted()
    {
        using var metrics = new MetricCapture("Croft.Web").Start();
        var logger = new CapturingLogger<TokenSessionEventsMarker>();
        var context = NewCookieContext(UserWithSid(FullSessionId), new InMemoryTokenStore(), logger);

        await TokenSessionEvents.OnValidatePrincipal(context);

        context.Principal.Should().BeNull();
        var entry = logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information).Which;
        entry.Properties["Reason"].Should().Be("EntryMissing");
        entry.Properties["SessionId"].Should().Be("01234567");
        logger.AllText.Should().NotContain(FullSessionId);
        metrics.Measurements.Should().ContainSingle(m =>
            m.Instrument == "croft.web.session.removed" && m.Value == 1 && m.HasTag("reason", "EntryMissing"));
    }

    [Fact]
    public async Task Purged_IsLoggedOnceWithItsCountAndCounted()
    {
        using var metrics = new MetricCapture("Croft.Web").Start();
        await using var db = CreateDbContext();
        db.UserSessions.Add(new UserSession { SessionId = Guid.NewGuid().ToString("N"), AccessToken = "at", ExpiresAt = Now.AddDays(-40), SessionExpiresAt = Now.AddDays(-10) });
        db.UserSessions.Add(new UserSession { SessionId = Guid.NewGuid().ToString("N"), AccessToken = "at", ExpiresAt = Now.AddDays(-40), SessionExpiresAt = Now.AddDays(-9) });
        await db.SaveChangesAsync();
        var logger = new CapturingLogger<SessionPurgeHostedService>();
        using var services = new ServiceCollection().BuildServiceProvider();
        var purger = new SessionPurgeHostedService(
            services.GetRequiredService<IServiceScopeFactory>(), logger, new FakeTimeProvider(Now), new AuthSettings(TimeSpan.FromDays(30)));

        var result = await purger.PurgeAsync(db);

        var entry = logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information).Which;
        entry.Properties["Reason"].Should().Be("Purged");
        entry.Properties["Count"].Should().Be(result.Purged);
        result.Purged.Should().BeGreaterThanOrEqualTo(2);
        metrics.Measurements.Should().ContainSingle(m =>
            m.Instrument == "croft.web.session.removed" && m.Value == result.Purged && m.HasTag("reason", "Purged"));
    }

    [Fact]
    public async Task BackChannelLogout_IsLoggedOnceWithItsCountAndCounted()
    {
        using var metrics = new MetricCapture("Croft.Web").Start();
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", new TokenEntry("at-1", "rt-1", Now.AddHours(1), Sid: "idp-sid-1"));
        await store.SetAsync("s2", new TokenEntry("at-2", "rt-2", Now.AddHours(1), Sid: "idp-sid-1"));
        var logger = new CapturingLogger<BackchannelLogoutService>();
        using var rsa = RSA.Create(2048);
        var configuration = new OpenIdConnectConfiguration { Issuer = "http://idp.example.com" };
        configuration.SigningKeys.Add(new RsaSecurityKey(rsa) { KeyId = "test-key" });
        var validator = new BackchannelLogoutTokenValidator(
            "blog-web",
            new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration), new FakeTimeProvider(Now));
        var logoutToken = SignedTestJwt.Create(rsa, "test-key", new Dictionary<string, object>
        {
            ["iss"] = "http://idp.example.com",
            ["aud"] = "blog-web",
            ["iat"] = Now.ToUnixTimeSeconds(),
            ["jti"] = "test-jti-1",
            ["sid"] = "idp-sid-1",
            ["events"] = new Dictionary<string, object>
            {
                ["http://schemas.openid.net/event/backchannel-logout"] = new Dictionary<string, object>()
            }
        });
        var service = new BackchannelLogoutService(store, validator, logger);

        var accepted = await service.LogoutAsync(logoutToken, CancellationToken.None);

        accepted.Should().BeTrue();
        var entry = logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information).Which;
        entry.Properties["Reason"].Should().Be("BackChannelLogout");
        entry.Properties["Count"].Should().Be(2);
        logger.AllText.Should().NotContain(logoutToken);
        metrics.Measurements.Should().ContainSingle(m =>
            m.Instrument == "croft.web.session.removed" && m.Value == 2 && m.HasTag("reason", "BackChannelLogout"));
    }

    private CroftWebDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<CroftWebDbContext>().UseNpgsql(fixture.ConnectionString).Options);

    private static UserAccessTokenService NewTokenService(
        InMemoryTokenStore store, HttpStatusCode status, string body, ILogger<UserAccessTokenService> logger) =>
        new(store,
            store,
            new SessionTestSupport.StaticHttpClientFactory(new SessionTestSupport.CannedTokenEndpoint(status, body)),
            new SessionTestSupport.StaticOptionsMonitor(SessionTestSupport.OidcOptions()),
            new FakeTimeProvider(Now),
            new AuthSettings(TimeSpan.FromDays(30)),
            logger);

    private static ClaimsPrincipal UserWithSid(string sessionId) =>
        new(new ClaimsIdentity([new Claim("sub", "admin-id"), new Claim("blog_sid", sessionId)], "test"));

    private static CookieValidatePrincipalContext NewCookieContext(
        ClaimsPrincipal user, ITokenStore store, ILogger logger)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton<ILoggerFactory>(new SingleLoggerFactory(logger));
        services.AddSingleton<IAuthenticationService>(new NoopAuthenticationService());
        return new CookieValidatePrincipalContext(
            new DefaultHttpContext { RequestServices = services.BuildServiceProvider() },
            new AuthenticationScheme("Cookies", null, typeof(CookieAuthenticationHandler)),
            new CookieAuthenticationOptions(),
            new AuthenticationTicket(user, new AuthenticationProperties(), "Cookies"));
    }

    /// <summary>The category the static <c>TokenSessionEvents</c> logs under.</summary>
    private sealed class TokenSessionEventsMarker;

    private sealed class SingleLoggerFactory(ILogger logger) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => logger;

        public void AddProvider(ILoggerProvider provider) { }

        public void Dispose() { }
    }

    private sealed class NoopAuthenticationService : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.NoResult());

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => Task.CompletedTask;

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    }
}
