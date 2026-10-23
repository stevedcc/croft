using AwesomeAssertions;
using Croft.Web.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests;

/// <summary>
/// Brief 13i item 8 (A-13): the auth cookie lives as long as the session does. The session store
/// ends a login at Auth:SessionLifetime (brief 13b), so a cookie that expires earlier signs a staff
/// member out of a session that is still valid.
/// </summary>
public class AuthCookieLifetimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("16.00:00:00", 16)]
    [InlineData("45.00:00:00", 45)]
    [InlineData(null, 30)]
    public async Task TheIssuedCookie_ExpiresAfterTheConfiguredSessionLifetime(string? lifetime, int expectedDays)
    {
        using var factory = CreateFactory(new FakeTimeProvider(Now), lifetime);
        var store = factory.Services.GetRequiredService<ITokenStore>();
        await store.SetAsync("s1", new TokenEntry("at", "rt", Now.AddHours(1), SessionExpiresAt: Now.AddDays(expectedDays)));
        var cookie = await AuthCookie.SignInAsync(factory.Services, "s1", "Admin");

        var result = await AuthCookie.AuthenticateAsync(factory.Services, cookie);

        result.Succeeded.Should().BeTrue("the control: the cookie authenticates");
        var ticket = result.Properties!;
        (ticket.ExpiresUtc!.Value - ticket.IssuedUtc!.Value).Should().Be(TimeSpan.FromDays(expectedDays),
            "the cookie ticket lasts as long as Auth:SessionLifetime (the default is 30 days)");
    }

    private static WebApplicationFactory<Program> CreateFactory(TimeProvider clock, string? lifetime) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("Oidc:Authority", "http://idp.example.com");
            builder.UseSetting("Oidc:ClientId", "blog-web");
            if (lifetime is not null)
            {
                builder.UseSetting("Auth:SessionLifetime", lifetime);
            }

            builder.ConfigureTestServices(services => services.AddSingleton(clock));
        });
}
