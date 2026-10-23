using System.Security.Cryptography;
using Croft.Web.Authentication;
using Croft.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using AwesomeAssertions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Croft.Web.Tests;

public class BackchannelLogoutServiceTests : IDisposable
{
    private const string Authority = "http://idp.example.com";
    private const string ClientId = "blog-web";
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly TokenEntry BaseEntry =
        new("at-1", "rt-1", Now.AddSeconds(3600));

    private readonly RSA _rsa = RSA.Create(2048);

    public void Dispose()
    {
        _rsa.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Logout_ValidTokenWithSid_RemovesOnlySessionsOfThatSid()
    {
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", BaseEntry with { Sid = "idp-sid-1", Sub = "admin-id" });
        await store.SetAsync("s2", BaseEntry with { Sid = "idp-sid-2", Sub = "admin-id" });
        await store.SetAsync("s3", BaseEntry with { Sub = "admin-id" });
        var service = CreateService(store);

        var accepted = await service.LogoutAsync(CreateLogoutToken(includeSid: true, sid: "idp-sid-1"), CancellationToken.None);

        accepted.Should().BeTrue();
        (await store.GetAsync("s1")).Should().BeNull("the logged-out session's tokens are gone");
        (await store.GetAsync("s2")).Should().NotBeNull("another session of the same user keeps its tokens");
        (await store.GetAsync("s3")).Should().NotBeNull();
    }

    [Fact]
    public async Task Logout_SidMatchingNoStoredSession_FallsBackToSubAndRemovesSessionsOfThatUser()
    {
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", BaseEntry with { Sub = "admin-id" });
        var service = CreateService(store);

        var accepted = await service.LogoutAsync(CreateLogoutToken(includeSid: true, sid: "idp-sid-1"), CancellationToken.None);

        accepted.Should().BeTrue();
        (await store.GetAsync("s1")).Should().BeNull("the stored session carries no sid, so the sub fallback must end it");
    }

    [Fact]
    public async Task Logout_ValidTokenWithSubOnly_RemovesEverySessionOfThatUser()
    {
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", BaseEntry with { Sid = "idp-sid-1", Sub = "admin-id" });
        await store.SetAsync("s2", BaseEntry with { Sid = "idp-sid-2", Sub = "admin-id" });
        await store.SetAsync("s3", BaseEntry with { Sid = "idp-sid-3", Sub = "editor-id" });
        var service = CreateService(store);

        var accepted = await service.LogoutAsync(CreateLogoutToken(), CancellationToken.None);

        accepted.Should().BeTrue();
        (await store.GetAsync("s1")).Should().BeNull();
        (await store.GetAsync("s2")).Should().BeNull("sub-only logout ends every session the user has");
        (await store.GetAsync("s3")).Should().NotBeNull("other users are untouched");
    }

    [Fact]
    public async Task Logout_InvalidToken_RemovesNothingAndReturnsFalse()
    {
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", BaseEntry with { Sid = "idp-sid-1", Sub = "admin-id" });
        var service = CreateService(store);

        var accepted = await service.LogoutAsync("not-a-valid-token", CancellationToken.None);

        accepted.Should().BeFalse("an invalid logout token must be answered with 400 and change nothing");
        (await store.GetAsync("s1")).Should().NotBeNull();
    }

    [Fact]
    public async Task Logout_MissingToken_ReturnsFalse()
    {
        var store = new InMemoryTokenStore();
        var service = CreateService(store);

        (await service.LogoutAsync(null, CancellationToken.None)).Should().BeFalse();
        (await service.LogoutAsync("", CancellationToken.None)).Should().BeFalse();
        (await service.LogoutAsync("   ", CancellationToken.None)).Should().BeFalse();
    }

    private BackchannelLogoutService CreateService(ITokenStore store)
    {
        var key = new RsaSecurityKey(_rsa) { KeyId = "test-key" };
        var configuration = new OpenIdConnectConfiguration();
        configuration.Issuer = Authority;
        configuration.SigningKeys.Add(key);
        var validator = new BackchannelLogoutTokenValidator(
            ClientId,
            new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration),
            new FakeTimeProvider(Now));
        return new BackchannelLogoutService(store, validator, NullLogger<BackchannelLogoutService>.Instance);
    }

    private string CreateLogoutToken(bool includeSid = false, string? sid = null)
    {
        var payload = new Dictionary<string, object>
        {
            ["iss"] = Authority,
            ["aud"] = ClientId,
            ["iat"] = Now.ToUnixTimeSeconds(),
            ["jti"] = "test-jti-1",
            ["sub"] = "admin-id",
            ["events"] = new Dictionary<string, object>
            {
                ["http://schemas.openid.net/event/backchannel-logout"] = new Dictionary<string, object>()
            }
        };
        if (includeSid)
        {
            payload["sid"] = sid ?? "idp-sid-1";
        }
        return SignedTestJwt.Create(_rsa, "test-key", payload);
    }
}
