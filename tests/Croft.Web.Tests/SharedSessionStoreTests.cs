using System.Net;
using System.Security.Cryptography;
using Croft.Web.Authentication;
using Croft.Web.Services;
using Croft.Web.Tests.Fixtures;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Croft.Web.Tests;

/// <summary>
/// With ConnectionStrings:WebDb set the token store is shared state: a session written by one Web
/// instance must be readable — and endable — through any other, so any instance can serve any
/// signed-in user and a back-channel logout ends the session everywhere. Two
/// WebApplicationFactory hosts on one database stand in for the two instances.
/// </summary>
[Trait("Category", "Container")]
public class SharedSessionStoreTests(PostgresDatabaseFixture fixture)
    : IClassFixture<PostgresDatabaseFixture>, IDisposable
{
    private const string Authority = "http://idp.example.com";
    private const string ClientId = "blog-web";
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private readonly RSA _rsa = RSA.Create(2048);

    public void Dispose()
    {
        _rsa.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task SessionWrittenByInstanceA_IsReadByInstanceB()
    {
        using var a = CreateFactory();
        using var b = CreateFactory();
        var storeA = a.Services.GetRequiredService<ITokenStore>();
        var storeB = b.Services.GetRequiredService<ITokenStore>();
        var entry = new TokenEntry(
            "at-1", "rt-1", InOneHour(), "idt-1", "idp-sid-1", "admin-id");

        await storeA.SetAsync("s1", entry);

        (await storeB.GetAsync("s1")).Should().Be(entry,
            "any Web instance must see the session another instance stored, or the user is signed out on the instance they did not log in on");
    }

    [Fact]
    public async Task BackChannelLogoutOnInstanceA_EndsTheSessionOnInstanceB()
    {
        using var a = CreateFactory();
        using var b = CreateFactory();
        var storeA = a.Services.GetRequiredService<ITokenStore>();
        var storeB = b.Services.GetRequiredService<ITokenStore>();
        await storeA.SetAsync("s1", new TokenEntry(
            "at-1", "rt-1", DateTimeOffset.UtcNow.AddHours(1), "idt-1", "idp-sid-1", "admin-id"));
        await storeB.SetAsync("s2", new TokenEntry(
            "at-2", "rt-2", DateTimeOffset.UtcNow.AddHours(1), "idt-2", "idp-sid-2", "admin-id"));

        var response = await a.CreateClient().PostAsync("/backchannel-logout", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["logout_token"] = CreateLogoutToken("idp-sid-1") }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await storeB.GetAsync("s1")).Should().BeNull(
            "a back-channel logout must end the session on every Web instance, not only the one that received it");
        (await storeB.GetAsync("s2")).Should().NotBeNull("only the logged-out sid's session goes");
    }

    [Fact]
    public async Task BackChannelLogoutWithOnlyASub_EndsEverySessionOfThatUserOnEveryInstance_AndNoOtherUsers()
    {
        // RemoveBySubAsync on the production (database) store: a logout token that names the user
        // but no session ends all of that user's sessions and nobody else's. Two users, two
        // sessions each, spread over both instances.
        using var a = CreateFactory();
        using var b = CreateFactory();
        var storeA = a.Services.GetRequiredService<ITokenStore>();
        var storeB = b.Services.GetRequiredService<ITokenStore>();
        var user = $"logout-user-{Guid.NewGuid():N}";
        var other = $"other-user-{Guid.NewGuid():N}";
        var ids = new[] { "s1", "s2", "s3", "s4" }.Select(id => $"{id}-{Guid.NewGuid():N}").ToArray();
        await storeA.SetAsync(ids[0], new TokenEntry("at-1", "rt-1", InOneHour(), "idt-1", "idp-sid-1", user));
        await storeB.SetAsync(ids[1], new TokenEntry("at-2", "rt-2", InOneHour(), "idt-2", "idp-sid-2", user));
        await storeA.SetAsync(ids[2], new TokenEntry("at-3", "rt-3", InOneHour(), "idt-3", "idp-sid-3", other));
        await storeB.SetAsync(ids[3], new TokenEntry("at-4", "rt-4", InOneHour(), "idt-4", "idp-sid-4", other));

        var response = await a.CreateClient().PostAsync("/backchannel-logout", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["logout_token"] = CreateLogoutToken(sid: null, sub: user) }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await storeB.GetAsync(ids[0])).Should().BeNull("the user's session on the instance that took the request ends");
        (await storeA.GetAsync(ids[1])).Should().BeNull("the user's session on the other instance ends too");
        (await storeB.GetAsync(ids[2])).Should().NotBeNull("another user's session is not touched");
        (await storeA.GetAsync(ids[3])).Should().NotBeNull("another user's session is not touched");
    }

    // One hour out, whole seconds, computed at call time: an already-expired session is the
    // purge job's dead weight (the hosts under test run that job), and the column round-trips
    // timestamps at microsecond precision — a clock value's sub-microsecond ticks cannot
    // survive the round-trip this test compares through.
    private static DateTimeOffset InOneHour()
    {
        var now = DateTimeOffset.UtcNow;
        return new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, TimeSpan.Zero)
            .AddHours(1);
    }

    // The host runs the real app (shared store registration included); only the logout token's
    // signing keys are swapped for a test key so the signature checks run against something known.
    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("ConnectionStrings:WebDb", fixture.ConnectionString);
            builder.UseSetting("Oidc:Authority", Authority);
            builder.UseSetting("Oidc:ClientId", ClientId);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<BackchannelLogoutTokenValidator>();
                services.AddSingleton(CreateValidator());
            });
        });

    private BackchannelLogoutTokenValidator CreateValidator()
    {
        var configuration = new OpenIdConnectConfiguration();
        configuration.Issuer = Authority;
        configuration.SigningKeys.Add(new RsaSecurityKey(_rsa) { KeyId = "test-key" });
        return new BackchannelLogoutTokenValidator(
            ClientId,
            new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration),
            new FakeTimeProvider(Now));
    }

    private string CreateLogoutToken(string? sid, string sub = "admin-id")
    {
        var payload = new Dictionary<string, object>
        {
            ["iss"] = Authority,
            ["aud"] = ClientId,
            ["iat"] = Now.ToUnixTimeSeconds(),
            ["jti"] = $"test-jti-{Guid.NewGuid():N}",
            ["sub"] = sub,
            ["events"] = new Dictionary<string, object>
            {
                [BackchannelLogoutTokenValidator.LogoutEvent] = new Dictionary<string, object>()
            }
        };
        if (sid is not null)
        {
            payload["sid"] = sid;
        }

        return SignedTestJwt.Create(_rsa, "test-key", payload);
    }
}
