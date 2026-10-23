using Microsoft.AspNetCore.DataProtection;
using System.Net;
using System.Security.Claims;
using Croft.Web.Data;
using Croft.Web.Options;
using Croft.Web.Services;
using Croft.Web.Tests.Fixtures;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Croft.Web.Tests;

/// <summary>
/// A login session lives as long as its refresh token, not its access token: the IdP issues
/// 5-minute access tokens and 30-day refresh tokens, and a session idle for ten minutes must
/// still be there (and refreshable) the next time the user comes back.
/// </summary>
[Trait("Category", "Container")]
public class SessionLifetimeTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    // The token store keeps tokens protected (brief 13b): rows are seeded and read the way the store writes them.
    private static readonly IDataProtectionProvider Protection = DataProtectionProvider.Create("test");
    private static readonly IDataProtector Protector = Protection.CreateProtector(PostgresTokenStore.Purpose);

    [Fact]
    public async Task IdleSession_WithAnExpiredAccessToken_SurvivesThePurge_AndRefreshesAndMovesItsSessionExpiryForward()
    {
        var sessionId = NewSessionId();
        var oldSessionExpiry = Now.AddDays(29);
        await SeedAsync(sessionId, expiresAt: Now.AddMinutes(-10), sessionExpiresAt: oldSessionExpiry);

        var result = await NewPurger().PurgeAsync(CreateDbContext());

        result.Skipped.Should().BeFalse();
        (await FindAsync(sessionId)).Should().NotBeNull(
            "the refresh token is valid for 29 more days, so an expired access token must not cost the user the session");

        var token = await NewTokenService().GetAccessTokenAsync(UserWithSid(sessionId));

        token.Should().Be("at-new", "the surviving session refreshes through the token endpoint");
        var refreshed = await FindAsync(sessionId);
        Protector.Unprotect(refreshed!.AccessToken).Should().Be("at-new");
        refreshed.SessionExpiresAt.Should().BeAfter(oldSessionExpiry,
            "every refresh moves the session's end forward with the refresh token's new lifetime");
    }

    [Fact]
    public async Task Session_WhoseSessionExpiryPassed_IsPurged()
    {
        var sessionId = NewSessionId();
        await SeedAsync(sessionId, expiresAt: Now.AddDays(-31), sessionExpiresAt: Now.AddMinutes(-1));

        await NewPurger().PurgeAsync(CreateDbContext());

        (await FindAsync(sessionId)).Should().BeNull("the session ended with its refresh token");
    }

    [Fact]
    public async Task LegacyRowWithoutSessionExpiry_IsPurgedOnceItsAccessTokenExpiredLongerAgoThanASessionMayLive()
    {
        var stale = NewSessionId();
        var recent = NewSessionId();
        await SeedAsync(stale, expiresAt: Now.AddDays(-31), sessionExpiresAt: null);
        await SeedAsync(recent, expiresAt: Now.AddDays(-1), sessionExpiresAt: null);

        await NewPurger().PurgeAsync(CreateDbContext());

        (await FindAsync(stale)).Should().BeNull("a row from a release that did not know the column ends with the default lifetime");
        (await FindAsync(recent)).Should().NotBeNull("a legacy row inside the default lifetime may still be refreshable");
    }

    [Fact]
    public async Task Migration_GivesAnExistingRowItsAccessTokenExpiryPlusThirtyDays()
    {
        var sessionId = NewSessionId();
        var accessTokenExpiry = new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);
        await using var db = CreateDbContext();
        var migrator = db.GetService<IMigrator>();
        const string previous = "20261012060542_AddDataProtectionKeys";

        await migrator.MigrateAsync(previous);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "UserSessions" ("SessionId", "AccessToken", "ExpiresAt")
            VALUES ({sessionId}, 'at-existing', {accessTokenExpiry})
            """);
        await migrator.MigrateAsync();

        (await FindAsync(sessionId))!.SessionExpiresAt.Should().Be(accessTokenExpiry.AddDays(30),
            "nobody signed in at deployment time may be purged by the deployment");
    }

    private static string NewSessionId() => Guid.NewGuid().ToString("N");

    private async Task SeedAsync(string sessionId, DateTimeOffset expiresAt, DateTimeOffset? sessionExpiresAt)
    {
        await using var db = CreateDbContext();
        db.UserSessions.Add(new UserSession
        {
            SessionId = sessionId,
            Sub = "admin-id",
            AccessToken = Protector.Protect("at-old"),
            RefreshToken = Protector.Protect("rt-old"),
            ExpiresAt = expiresAt,
            SessionExpiresAt = sessionExpiresAt
        });
        await db.SaveChangesAsync();
    }

    private async Task<UserSession?> FindAsync(string sessionId)
    {
        await using var db = CreateDbContext();
        return await db.UserSessions.AsNoTracking().SingleOrDefaultAsync(s => s.SessionId == sessionId);
    }

    private CroftWebDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<CroftWebDbContext>().UseNpgsql(fixture.ConnectionString).Options);

    private static SessionPurgeHostedService NewPurger()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        return new SessionPurgeHostedService(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SessionPurgeHostedService>.Instance,
            new FakeTimeProvider(Now),
            new AuthSettings(TimeSpan.FromDays(30)));
    }

    private UserAccessTokenService NewTokenService()
    {
        var factory = new ServiceCollection()
            .AddDbContextFactory<CroftWebDbContext>(options => options.UseNpgsql(fixture.ConnectionString))
            .BuildServiceProvider()
            .GetRequiredService<IDbContextFactory<CroftWebDbContext>>();
        var store = new PostgresTokenStore(factory, Protection, new FakeTimeProvider(Now));
        var endpoint = new SessionTestSupport.CannedTokenEndpoint(
            HttpStatusCode.OK, """{"access_token":"at-new","refresh_token":"rt-new","expires_in":300,"refresh_expires_in":2592000}""");
        return new UserAccessTokenService(
            store,
            store,
            new SessionTestSupport.StaticHttpClientFactory(endpoint),
            new SessionTestSupport.StaticOptionsMonitor(SessionTestSupport.OidcOptions()),
            new FakeTimeProvider(Now),
            new AuthSettings(TimeSpan.FromDays(30)),
            NullLogger<UserAccessTokenService>.Instance);
    }

    private static ClaimsPrincipal UserWithSid(string sessionId) =>
        new(new ClaimsIdentity([new Claim("sub", "admin-id"), new Claim("blog_sid", sessionId)], "test"));
}
