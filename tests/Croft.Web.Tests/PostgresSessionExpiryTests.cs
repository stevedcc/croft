using System.Net;
using Croft.Web.Data;
using Croft.Web.Options;
using Croft.Web.Services;
using Croft.Web.Tests.Fixtures;
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Croft.Web.Tests;

/// <summary>
/// Brief 13b item 5 for the shared store: a row past its session expiry reads as missing before the
/// purge job has deleted it, so the cookie is rejected and no refresh request reaches the IdP.
/// </summary>
[Trait("Category", "Container")]
public class PostgresSessionExpiryTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ASessionOneSecondPastItsExpiry_IsMissing_OnTheReadAndTheLockedPath()
    {
        var store = NewStore(new FakeTimeProvider(Now));
        await store.SetAsync("ended", Entry(Now.AddSeconds(-1)));
        await store.SetAsync("live", Entry(Now.AddSeconds(1)));

        (await store.GetAsync("ended")).Should().BeNull();
        (await store.GetAsync("live")).Should().NotBeNull();
        (await store.RunLockedAsync("ended", (session, _) => Task.FromResult(session.Entry))).Should().BeNull();
    }

    [Fact]
    public async Task TheTokenService_NeverRefreshesAnEndedSession_AndNoRequestReachesTheIdP()
    {
        var clock = new FakeTimeProvider(Now);
        var store = NewStore(clock);
        await store.SetAsync("ended", Entry(Now.AddSeconds(-1)));
        await store.SetAsync("live", Entry(Now.AddDays(1)));
        var idp = new SessionTestSupport.CannedTokenEndpoint(HttpStatusCode.OK,
            """{"access_token":"at-new","refresh_token":"rt-new","expires_in":300,"refresh_expires_in":2592000}""");
        var service = new UserAccessTokenService(store, store,
            new SessionTestSupport.StaticHttpClientFactory(idp),
            new SessionTestSupport.StaticOptionsMonitor(SessionTestSupport.OidcOptions()),
            clock, new AuthSettings(TimeSpan.FromDays(30)), NullLogger<UserAccessTokenService>.Instance);

        (await service.GetAccessTokenAsync(User("ended"))).Should().BeNull();
        idp.RequestCount.Should().Be(0);
        (await service.GetAccessTokenAsync(User("live"))).Should().Be("at-new", "the control: a live session is refreshed");
    }

    [Fact]
    public async Task TheNextRequest_WithTheCookieOfAnEndedSession_IsSignedOut()
    {
        var clock = new FakeTimeProvider(Now);
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("ConnectionStrings:WebDb", fixture.ConnectionString);
            builder.UseSetting("Oidc:Authority", "http://idp.example.com");
            builder.UseSetting("Oidc:ClientId", "blog-web");
            builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(clock));
        });
        await factory.Services.GetRequiredService<ITokenStore>().SetAsync("s-pg-live", Entry(Now.AddSeconds(30)));
        var cookie = await AuthCookie.SignInAsync(factory.Services, "s-pg-live", "Admin");

        (await AuthCookie.AuthenticateAsync(factory.Services, cookie)).Succeeded.Should().BeTrue("the control: the session is alive");

        clock.UtcNow = Now.AddSeconds(31);

        (await AuthCookie.AuthenticateAsync(factory.Services, cookie)).Succeeded.Should().BeFalse();
    }

    private static TokenEntry Entry(DateTimeOffset sessionExpiresAt) =>
        new("at-old", "rt-old", Now.AddMinutes(-10), "idt", "sid", "sub", sessionExpiresAt);

    private static System.Security.Claims.ClaimsPrincipal User(string sessionId) =>
        new(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim("sub", "sub"), new System.Security.Claims.Claim("blog_sid", sessionId)], "test"));

    private PostgresTokenStore NewStore(TimeProvider clock)
    {
        var factory = new ServiceCollection()
            .AddDbContextFactory<CroftWebDbContext>(options => options.UseNpgsql(fixture.ConnectionString))
            .BuildServiceProvider()
            .GetRequiredService<IDbContextFactory<CroftWebDbContext>>();
        return new PostgresTokenStore(factory, DataProtectionProvider.Create("test"), clock);
    }
}
